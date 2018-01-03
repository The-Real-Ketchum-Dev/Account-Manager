﻿#region using directives

using Newtonsoft.Json;
using POGOLib.Official;
using POGOLib.Official.Exceptions;
using POGOLib.Official.LoginProviders;
using POGOLib.Official.Net;
using POGOLib.Official.Net.Authentication;
using POGOLib.Official.Net.Authentication.Data;
using POGOLib.Official.Net.Captcha;
using POGOLib.Official.Util.Device;
using POGOLib.Official.Util.Hash;
using POGOProtos.Data;
using POGOProtos.Networking.Requests.Messages;
using POGOProtos.Networking.Responses;
using PokemonGoGUI.Enums;
using PokemonGoGUI.Exceptions;
using PokemonGoGUI.Extensions;
using PokemonGoGUI.GoManager;
using PokemonGoGUI.GoManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using static POGOProtos.Networking.Envelopes.Signature.Types;

#endregion

namespace PokemonGoGUI
{
    public class Client
    {
        public ProxyEx Proxy;
        public Version VersionStr;
        public uint AppVersion;
        public Session ClientSession;
        public bool LoggedIn = false;
        private GetPlayerMessage.Types.PlayerLocale PlayerLocale;
        private DeviceWrapper ClientDeviceWrapper;
        public Manager ClientManager;

        public Client()
        {
            VersionStr = new Version("0.87.5");
            AppVersion = 8700;
        }

        public void Logout()
        {
            if (!LoggedIn)
                return;
            LoggedIn = false;
            ClientSession.AssetDigestUpdated -= OnAssetDisgestReceived;
            ClientSession.ItemTemplatesUpdated -= OnItemTemplatesReceived;
            ClientSession.UrlsUpdated -= OnDownloadUrlsReceived;
            ClientSession.LocalConfigUpdated -= OnLocalConfigVersionReceived;
            ClientSession.AccessTokenUpdated -= SessionAccessTokenUpdated;
            ClientSession.CaptchaReceived -= SessionOnCaptchaReceived;
            ClientSession.InventoryUpdate -= SessionInventoryUpdate;
            ClientSession.MapUpdate -= SessionMapUpdate;
            ClientSession.CheckAwardedBadgesReceived -= OnCheckAwardedBadgesReceived;
            ClientSession.HatchedEggsReceived -= OnHatchedEggsReceived;
            ClientSession.Shutdown();
        }

        public async Task<MethodResult<bool>> DoLogin(Manager manager)
        {
            SetSettings(manager);
            // TODO: see how do this only once better.
            if (!(Configuration.Hasher is PokeHashHasher))
            {
                // By default Configuration.Hasher is LegacyHasher type  (see Configuration.cs in the pogolib source code)
                // -> So this comparation only will run once.
                if (ClientManager.UserSettings.UseOnlyOneKey)
                {
                    Configuration.Hasher = new PokeHashHasher(ClientManager.UserSettings.AuthAPIKey);
                    Configuration.HasherUrl = ClientManager.UserSettings.HashHost;
                    Configuration.HashEndpoint = ClientManager.UserSettings.HashEndpoint;
                }
                else
                    Configuration.Hasher = new PokeHashHasher(ClientManager.UserSettings.HashKeys.ToArray());

                // TODO: make this configurable. To avoid bans (may be with a checkbox in hash keys tab).
                //Configuration.IgnoreHashVersion = true;
                VersionStr = Configuration.Hasher.PokemonVersion;
                AppVersion = Configuration.Hasher.AppVersion;
                // TODO: Revise sleeping
                //((PokeHashHasher)Configuration.Hasher).PokehashSleeping += OnPokehashSleeping;
            }
            // *****

            ILoginProvider loginProvider;

            switch (ClientManager.UserSettings.AuthType)
            {
                case AuthType.Google:
                    loginProvider = new GoogleLoginProvider(ClientManager.UserSettings.Username, ClientManager.UserSettings.Password);
                    break;
                case AuthType.Ptc:
                    loginProvider = new PtcLoginProvider(ClientManager.UserSettings.Username, ClientManager.UserSettings.Password, Proxy.AsWebProxy());
                    break;
                default:
                    throw new ArgumentException("Login provider must be either \"google\" or \"ptc\".");
            }

            ClientSession = await GetSession(loginProvider, ClientManager.UserSettings.DefaultLatitude, ClientManager.UserSettings.DefaultLongitude, true);

            // Send initial requests and start HeartbeatDispatcher.
            // This makes sure that the initial heartbeat request finishes and the "session.Map.Cells" contains stuff.
            var msgStr = "Session couldn't start up.";
            LoggedIn = false;
            try
            {
                //My files resources here
                var filename = "data/"+ ClientManager.UserSettings.DeviceId+"IT.json";
                if (File.Exists(filename))
                    ClientSession.Templates.ItemTemplates = Serializer.FromJson<List<DownloadItemTemplatesResponse.Types.ItemTemplate>>(File.ReadAllText(filename));
                filename = "data/"+ ClientManager.UserSettings.DeviceId+"UR.json";
                if (File.Exists(filename))
                    ClientSession.Templates.DownloadUrls = Serializer.FromJson<List<DownloadUrlEntry>>(File.ReadAllText(filename));
                filename = "data/"+ ClientManager.UserSettings.DeviceId+"AD.json";
                if (File.Exists(filename))
                    ClientSession.Templates.AssetDigests = Serializer.FromJson<List<AssetDigestEntry>>(File.ReadAllText(filename));
                filename = "data/"+ ClientManager.UserSettings.DeviceId+"LCV.json";
                if (File.Exists(filename))
                    ClientSession.Templates.LocalConfigVersion = Serializer.FromJson<DownloadRemoteConfigVersionResponse>(File.ReadAllText(filename));

                if (await ClientSession.StartupAsync(true))
                {
                    LoggedIn = true;
                    msgStr = "Successfully logged into server.";

                    ClientSession.AssetDigestUpdated += OnAssetDisgestReceived;
                    ClientSession.ItemTemplatesUpdated += OnItemTemplatesReceived;
                    ClientSession.UrlsUpdated += OnDownloadUrlsReceived;
                    ClientSession.LocalConfigUpdated += OnLocalConfigVersionReceived;
                    ClientSession.AccessTokenUpdated += SessionAccessTokenUpdated;
                    ClientSession.CaptchaReceived += SessionOnCaptchaReceived;
                    ClientSession.InventoryUpdate += SessionInventoryUpdate;
                    ClientSession.MapUpdate += SessionMapUpdate;
                    ClientSession.CheckAwardedBadgesReceived += OnCheckAwardedBadgesReceived;
                    ClientSession.HatchedEggsReceived += OnHatchedEggsReceived;

                    ClientManager.LogCaller(new LoggerEventArgs("Succefully added all events to the client.", LoggerTypes.Debug));

                    SaveAccessToken(ClientSession.AccessToken);
                }
            }
            catch (PtcOfflineException)
            {
                ClientManager.Stop();

                ClientManager.LogCaller(new LoggerEventArgs("Ptc server offline. Please try again later.", LoggerTypes.Warning));

                return new MethodResult<bool>
                {
                    Message = "Ptc server offline."
                };
            }
            catch (AccountNotVerifiedException)
            {
                ClientManager.Stop();
                ClientManager.RemoveProxy();

                ClientManager.LogCaller(new LoggerEventArgs("Account not verified. Stopping ...", LoggerTypes.Warning));

                ClientManager.AccountState = Enums.AccountState.NotVerified;

                return new MethodResult<bool>
                {
                    Message = "Account not verified."
                };
            }
            catch (WebException ex)
            {
                ClientManager.Stop();

                if (ex.Status == WebExceptionStatus.Timeout)
                {
                    if (String.IsNullOrEmpty(ClientManager.Proxy))
                    {
                        ClientManager.LogCaller(new LoggerEventArgs("Login request has timed out.", LoggerTypes.Warning));
                    }
                    else
                    {
                        ClientManager._proxyIssue = true;
                        ClientManager.LogCaller(new LoggerEventArgs("Login request has timed out. Possible bad proxy.", LoggerTypes.ProxyIssue));
                    }

                    return new MethodResult<bool>
                    {
                        Message = "Request has timed out."
                    };
                }

                if (!String.IsNullOrEmpty(ClientManager.Proxy))
                {
                    if (ex.Status == WebExceptionStatus.ConnectionClosed)
                    {
                        ClientManager._proxyIssue = true;
                        ClientManager.LogCaller(new LoggerEventArgs("Potential http proxy detected. Only https proxies will work.", LoggerTypes.ProxyIssue));

                        return new MethodResult<bool>
                        {
                            Message = "Http proxy detected"
                        };
                    }
                    else if (ex.Status == WebExceptionStatus.ConnectFailure || ex.Status == WebExceptionStatus.ProtocolError || ex.Status == WebExceptionStatus.ReceiveFailure
                        || ex.Status == WebExceptionStatus.ServerProtocolViolation)
                    {
                        ClientManager._proxyIssue = true;
                        ClientManager.LogCaller(new LoggerEventArgs("Proxy is offline", LoggerTypes.ProxyIssue));

                        return new MethodResult<bool>
                        {
                            Message = "Proxy is offline"
                        };
                    }
                }

                ClientManager._proxyIssue |= !String.IsNullOrEmpty(ClientManager.Proxy);

                ClientManager.LogCaller(new LoggerEventArgs("Failed to login due to request error", LoggerTypes.Exception, ex.InnerException));

                return new MethodResult<bool>
                {
                    Message = "Failed to login due to request error"
                };
            }
            catch (TaskCanceledException)
            {
                ClientManager.Stop();

                if (String.IsNullOrEmpty(ClientManager.Proxy))
                {
                    ClientManager.LogCaller(new LoggerEventArgs("Login request has timed out", LoggerTypes.Warning));
                }
                else
                {
                    ClientManager._proxyIssue = true;
                    ClientManager.LogCaller(new LoggerEventArgs("Login request has timed out. Possible bad proxy", LoggerTypes.ProxyIssue));
                }

                return new MethodResult<bool>
                {
                    Message = "Login request has timed out"
                };
            }
            catch (InvalidCredentialsException ex)
            {
                //Puts stopping log before other log.
                ClientManager.Stop();
                ClientManager.RemoveProxy();

                ClientManager.LogCaller(new LoggerEventArgs("Invalid credentials or account lockout. Stopping bot...", LoggerTypes.Warning, ex));

                return new MethodResult<bool>
                {
                    Message = "Username or password incorrect"
                };
            }
            catch (IPBannedException)
            {
                if (ClientManager.UserSettings.StopOnIPBan)
                {
                    ClientManager.Stop();
                }

                string message = String.Empty;

                if (!String.IsNullOrEmpty(ClientManager.Proxy))
                {
                    if (ClientManager.CurrentProxy != null)
                    {
                        ClientManager.ProxyHandler.MarkProxy(ClientManager.CurrentProxy, true);
                    }

                    message = "Proxy IP is banned.";
                }
                else
                {
                    message = "IP address is banned.";
                }

                ClientManager._proxyIssue = true;

                ClientManager.LogCaller(new LoggerEventArgs(message, LoggerTypes.ProxyIssue));

                return new MethodResult<bool>
                {
                    Message = message
                };
            }
            catch (GoogleLoginException ex)
            {
                ClientManager.Stop();
                ClientManager.RemoveProxy();

                ClientManager.LogCaller(new LoggerEventArgs(ex.Message, LoggerTypes.Warning));

                return new MethodResult<bool>
                {
                    Message = "Failed to login"
                };
            }
            catch (Exception ex)
            {
                ClientManager.Stop();
                //RemoveProxy();

                ClientManager.LogCaller(new LoggerEventArgs("Failed to login", LoggerTypes.Exception, ex));

                return new MethodResult<bool>
                {
                    Message = "Failed to login"
                };
            }
            return new MethodResult<bool>()
            {
                Success = LoggedIn,
                Message = msgStr
            };
        }

        private void OnAssetDisgestReceived(object sender, List<POGOProtos.Data.AssetDigestEntry> data)
        {
            var filename = "data/"+ ClientManager.UserSettings.DeviceId+"AD.json";
            if (!Directory.Exists("data"))
                Directory.CreateDirectory("data");
            File.WriteAllText(filename,Serializer.ToJson(data));
        }

        private void OnItemTemplatesReceived(object sender, List<DownloadItemTemplatesResponse.Types.ItemTemplate> data)
        {
            var filename = "data/"+ ClientManager.UserSettings.DeviceId+"IT.json";
            if (!Directory.Exists("data"))
                Directory.CreateDirectory("data");
            File.WriteAllText(filename,Serializer.ToJson(data));
        }

        private void OnDownloadUrlsReceived(object sender, List<POGOProtos.Data.DownloadUrlEntry> data)
        {
            var filename = "data/"+ ClientManager.UserSettings.DeviceId+"UR.json";
            if (!Directory.Exists("data"))
                Directory.CreateDirectory("data");
            File.WriteAllText(filename,Serializer.ToJson(data));
        }

        private void OnLocalConfigVersionReceived(object sender, DownloadRemoteConfigVersionResponse data)
        {
            var filename = "data/"+ ClientManager.UserSettings.DeviceId+"LCV.json";
            if (!Directory.Exists("data"))
                Directory.CreateDirectory("data");
            File.WriteAllText(filename,Serializer.ToJson(data));
        }

        private event EventHandler<int> OnPokehashSleeping;

        private void PokehashSleeping(object sender, int sleepTime)
        {
            OnPokehashSleeping?.Invoke(sender, sleepTime);
        }

        private void SessionMapUpdate(object sender, EventArgs e)
        {
            // Update BuddyPokemon Stats
            //var msg = $"BuddyWalked Candy: {ClientSession.Player.BuddyCandy}";
            //ClientManager.LogCaller(new LoggerEventArgs(msg, LoggerTypes.Success));
        }

        public void SessionOnCaptchaReceived(object sender, CaptchaEventArgs e)
        {
            ClientManager.AccountState = AccountState.CaptchaReceived;
            //2captcha needed to solve or chrome drive for solve url manual
            //e.CaptchaUrl;
        }

        private void SessionInventoryUpdate(object sender, EventArgs e)
        {
            //ClientManager.UpdateInventory();
        }

        private void OnHatchedEggsReceived(object sender, GetHatchedEggsResponse hatchedEggResponse)
        {
            //
        }

        private void OnCheckAwardedBadgesReceived(object sender, CheckAwardedBadgesResponse e)
        {
            //
        }

        private void SessionAccessTokenUpdated(object sender, EventArgs e)
        {
            SaveAccessToken(ClientSession.AccessToken);
        }

        public void SetSettings(Manager manager)
        {
            ClientManager = manager;

            int osId = OsVersions[ClientManager.UserSettings.FirmwareType.Length].Length;
            var firmwareUserAgentPart = OsUserAgentParts[osId];
            var firmwareType = OsVersions[osId];

            Proxy = new ProxyEx
            {
                Address = ClientManager.UserSettings.ProxyIP,
                Port = ClientManager.UserSettings.ProxyPort,
                Username = ClientManager.UserSettings.ProxyUsername,
                Password = ClientManager.UserSettings.ProxyPassword
            };

            ClientDeviceWrapper = new DeviceWrapper
            {
                UserAgent = $"pokemongo/1 {firmwareUserAgentPart}",
                DeviceInfo = new DeviceInfo
                {
                    DeviceId = ClientManager.UserSettings.DeviceId,
                    DeviceBrand = ClientManager.UserSettings.DeviceBrand,
                    DeviceModelBoot = ClientManager.UserSettings.DeviceModelBoot,
                    HardwareModel = ClientManager.UserSettings.HardwareModel,
                    HardwareManufacturer = ClientManager.UserSettings.HardwareManufacturer,
                    FirmwareBrand = ClientManager.UserSettings.FirmwareBrand,
                    FirmwareType = ClientManager.UserSettings.FirmwareType,
                    AndroidBoardName = ClientManager.UserSettings.AndroidBoardName,
                    AndroidBootloader = ClientManager.UserSettings.AndroidBootloader,
                    DeviceModel = ClientManager.UserSettings.DeviceModel,
                    DeviceModelIdentifier = ClientManager.UserSettings.DeviceModelIdentifier,
                    FirmwareFingerprint = ClientManager.UserSettings.FirmwareFingerprint,
                    FirmwareTags = ClientManager.UserSettings.FirmwareTags
                },
                Proxy = Proxy.AsWebProxy()
            };

            PlayerLocale = new GetPlayerMessage.Types.PlayerLocale
            {
                Country = ClientManager.UserSettings.Country,
                Language = ClientManager.UserSettings.Language,
                Timezone = ClientManager.UserSettings.TimeZone
            };
        }

        private void SaveAccessToken(AccessToken accessToken)
        {
            var fileName = Path.Combine(Directory.GetCurrentDirectory(), "Cache", $"{accessToken.Uid}.json");

            File.WriteAllText(fileName, JsonConvert.SerializeObject(accessToken, Formatting.Indented));
        }

        /// <summary>
        /// Login to PokémonGo and return an authenticated <see cref="ClientSession" />.
        /// </summary>
        /// <param name="loginProvider">Provider must be PTC or Google.</param>
        /// <param name="initLat">The initial latitude.</param>
        /// <param name="initLong">The initial longitude.</param>
        /// <param name="mayCache">Can we cache the <see cref="AccessToken" /> to a local file?</param>
        private async Task<Session> GetSession(ILoginProvider loginProvider, double initLat, double initLong, bool mayCache = false)
        {            
            var cacheDir = Path.Combine(Directory.GetCurrentDirectory(), "Cache");
            var fileName = Path.Combine(cacheDir, $"{loginProvider.UserId}-{loginProvider.ProviderId}.json");

            if (mayCache)
            {
                if (!Directory.Exists(cacheDir))
                    Directory.CreateDirectory(cacheDir);

                if (File.Exists(fileName))
                {
                    var accessToken = JsonConvert.DeserializeObject<AccessToken>(File.ReadAllText(fileName));

                    if (!accessToken.IsExpired)
                        return Login.GetSession(loginProvider, accessToken, initLat, initLong, ClientDeviceWrapper, PlayerLocale);
                }
            }

            var session = await Login.GetSession(loginProvider, initLat, initLong, ClientDeviceWrapper, PlayerLocale);

            if (mayCache)
                SaveAccessToken(session.AccessToken);

            return session;
        }

        private static readonly string[] OsUserAgentParts = {
            "CFNetwork/758.0.2 Darwin/15.0.0",  // 9.0
            "CFNetwork/758.0.2 Darwin/15.0.0",  // 9.0.1
            "CFNetwork/758.0.2 Darwin/15.0.0",  // 9.0.2
            "CFNetwork/758.1.6 Darwin/15.0.0",  // 9.1
            "CFNetwork/758.2.8 Darwin/15.0.0",  // 9.2
            "CFNetwork/758.2.8 Darwin/15.0.0",  // 9.2.1
            "CFNetwork/758.3.15 Darwin/15.4.0", // 9.3
            "CFNetwork/758.4.3 Darwin/15.5.0", // 9.3.2
            "CFNetwork/807.2.14 Darwin/16.3.0", // 10.3.3
            "CFNetwork/889.3 Darwin/17.2.0", // 11.1.0
            "CFNetwork/893.10 Darwin/17.3.0", // 11.2.0
        };

        private static readonly string[][] Devices =
        {
            new[] {"iPad5,1", "iPad", "J96AP"},
            new[] {"iPad5,2", "iPad", "J97AP"},
            new[] {"iPad5,3", "iPad", "J81AP"},
            new[] {"iPad5,4", "iPad", "J82AP"},
            new[] {"iPad6,7", "iPad", "J98aAP"},
            new[] {"iPad6,8", "iPad", "J99aAP"},
            new[] {"iPhone5,1", "iPhone", "N41AP"},
            new[] {"iPhone5,2", "iPhone", "N42AP"},
            new[] {"iPhone5,3", "iPhone", "N48AP"},
            new[] {"iPhone5,4", "iPhone", "N49AP"},
            new[] {"iPhone6,1", "iPhone", "N51AP"},
            new[] {"iPhone6,2", "iPhone", "N53AP"},
            new[] {"iPhone7,1", "iPhone", "N56AP"},
            new[] {"iPhone7,2", "iPhone", "N61AP"},
            new[] {"iPhone8,1", "iPhone", "N71AP"},
            new[] {"iPhone8,2", "iPhone", "MKTM2"}, //iphone 6s plus
            new[] {"iPhone9,3", "iPhone", "MN9T2"}
        };

        private static readonly string[] OsVersions = {
            "9.0",
            "9.0.1",
            "9.0.2",
            "9.1",
            "9.2",
            "9.2.1",
            "9.3",
            "9.3.2",
            "10.3.3",
            "11.1.0",
            "11.2.0"
        };
    }
}
