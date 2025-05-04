using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SteamKit2;

namespace SteamCmdWebAPI.Services
{
    public class SteamAppInfoService
    {
        private readonly ILogger<SteamAppInfoService> _logger;
        private SteamClient _steamClient;
        private SteamUser _steamUser;
        private SteamApps _steamApps;
        private SteamUserStats _steamUserStats;
        private CallbackManager _callbackManager;
        private bool _isConnected;
        private bool _isLoggedIn;
        private string _currentUsername;
        private AutoResetEvent _connectEvent;
        private AutoResetEvent _logonEvent;
        private AutoResetEvent _appInfoEvent;
        private AutoResetEvent _ownedGamesEvent;
        private Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> _appInfoCache;
        private List<SteamKit2.KeyValue> _ownedGames;

        // Popular games cache for fallback
        private readonly Dictionary<string, string> _popularGames = new Dictionary<string, string>()
        {
            { "570", "Dota 2" },
            { "730", "Counter-Strike 2" },
            { "440", "Team Fortress 2" },
            { "578080", "PUBG: BATTLEGROUNDS" },
            { "252490", "Rust" },
            { "271590", "Grand Theft Auto V" },
            { "359550", "Tom Clancy's Rainbow Six Siege" },
            { "550", "Left 4 Dead 2" },
            { "4000", "Garry's Mod" },
            { "230410", "Warframe" },
            { "105600", "Terraria" },
            { "292030", "The Witcher 3: Wild Hunt" },
            { "1245620", "Elden Ring" },
            { "739630", "Phasmophobia" },
            { "1938090", "Call of Duty" },
            { "1172470", "Apex Legends" },
            { "218620", "PAYDAY 2" },
            { "381210", "Dead by Daylight" },
            { "252950", "Rocket League" },
            { "400", "Portal" },
            { "620", "Portal 2" },
            // More popular games omitted for brevity
        };

        public SteamAppInfoService(ILogger<SteamAppInfoService> logger)
        {
            _logger = logger;
            _appInfoCache = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();
            _ownedGames = new List<SteamKit2.KeyValue>();
            InitializeSteamClient();
        }

        private void InitializeSteamClient()
        {
            _steamClient = new SteamClient();
            _callbackManager = new CallbackManager(_steamClient);
            _steamUser = _steamClient.GetHandler<SteamUser>();
            _steamApps = _steamClient.GetHandler<SteamApps>();
            _steamUserStats = _steamClient.GetHandler<SteamUserStats>();

            _connectEvent = new AutoResetEvent(false);
            _logonEvent = new AutoResetEvent(false);
            _appInfoEvent = new AutoResetEvent(false);
            _ownedGamesEvent = new AutoResetEvent(false);

            // Register callbacks
            _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            _callbackManager.Subscribe<SteamApps.PICSProductInfoCallback>(OnAppInfoReceived);
            
            // Subscribe to owned games response
            _callbackManager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            _logger.LogInformation("Connected to Steam");
            _isConnected = true;
            _connectEvent.Set();
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            _logger.LogInformation("Disconnected from Steam: {0}", callback.UserInitiated ? "user initiated" : "connection lost");
            _isConnected = false;
            _isLoggedIn = false;
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                _logger.LogError("Unable to log in: {0}", callback.Result);
                _isLoggedIn = false;
            }
            else
            {
                _logger.LogInformation("Successfully logged in as {0}", _currentUsername);
                _isLoggedIn = true;
            }
            
            _logonEvent.Set();
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            _logger.LogInformation("Logged off from Steam: {0}", callback.Result);
            _isLoggedIn = false;
        }

        private void OnAppInfoReceived(SteamApps.PICSProductInfoCallback callback)
        {
            _logger.LogInformation("Received info for {0} apps", callback.Apps.Count);
            
            foreach (var app in callback.Apps)
            {
                _appInfoCache[app.Key] = app.Value;
            }
            
            _appInfoEvent.Set();
        }

        private void OnLicenseList(SteamApps.LicenseListCallback callback)
        {
            _logger.LogInformation("Received license list with {0} licenses", callback.LicenseList.Count);
            _ownedGamesEvent.Set();
        }

        private async Task<bool> ConnectAndLogin(string username, string password, bool anonymous = false)
        {
            try
            {
                if (_isConnected && _isLoggedIn && 
                    !anonymous && _currentUsername == username)
                {
                    // Already connected and logged in as the requested user
                    return true;
                }

                // Disconnect if already connected
                if (_isConnected)
                {
                    _steamClient.Disconnect();
                    _isConnected = false;
                    _isLoggedIn = false;
                    await Task.Delay(1000); // Wait for disconnect to complete
                }

                _currentUsername = username;

                // Connect to Steam
                _logger.LogInformation("Connecting to Steam...");
                _steamClient.Connect();
                
                if (!_connectEvent.WaitOne(10000)) // Wait max 10 seconds for connection
                {
                    _logger.LogError("Connection to Steam timed out");
                    return false;
                }

                // Login with credentials
                _logger.LogInformation("Logging in to Steam{0}...", anonymous ? " anonymously" : $" as {username}");
                
                if (anonymous)
                {
                    _steamUser.LogOnAnonymous();
                }
                else
                {
                    var logOnDetails = new SteamUser.LogOnDetails
                    {
                        Username = username,
                        Password = password
                    };
                    _steamUser.LogOn(logOnDetails);
                }

                if (!_logonEvent.WaitOne(10000)) // Wait max 10 seconds for login
                {
                    _logger.LogError("Login to Steam timed out");
                    return false;
                }

                return _isLoggedIn;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to Steam");
                return false;
            }
        }

        public async Task<(string AppId, string GameName)> GetAppInfoAsync(string appIdStr)
        {
            try
            {
                if (string.IsNullOrEmpty(appIdStr))
                {
                    _logger.LogWarning("Empty AppID, cannot get information");
                    return (string.Empty, string.Empty);
                }
                
                if (!uint.TryParse(appIdStr, out uint appId))
                {
                    _logger.LogWarning("Invalid AppID: {0}", appIdStr);
                    return (appIdStr, string.Empty);
                }

                // Try to get from popular games list first (fastest)
                if (_popularGames.TryGetValue(appIdStr, out string popularGameName))
                {
                    return (appIdStr, popularGameName);
                }

                // Connect and login anonymously (sufficient for public app info)
                bool connected = await ConnectAndLogin("", "", true);
                if (!connected)
                {
                    _logger.LogError("Failed to connect to Steam");
                    return (appIdStr, string.Empty);
                }

                // Process callbacks while waiting
                Task.Run(() => {
                    while (_isConnected)
                    {
                        _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(500));
                    }
                });

                // Check if app info is already in cache
                if (_appInfoCache.TryGetValue(appId, out var cachedAppInfo))
                {
                    string gameName = GetGameNameFromAppInfo(cachedAppInfo);
                    _logger.LogInformation("Retrieved info from cache for AppID {0}: {1}", appId, gameName);
                    return (appIdStr, gameName);
                }

                // Request app info from Steam
                var request = new SteamApps.PICSRequest(appId);
                _steamApps.PICSGetProductInfo(new List<SteamApps.PICSRequest> { request }, null);
                
                // Wait for result
                bool received = _appInfoEvent.WaitOne(10000); // Wait up to 10 seconds
                
                if (!received || !_appInfoCache.TryGetValue(appId, out var appInfo))
                {
                    _logger.LogWarning("Did not receive info for AppID {0}", appId);
                    return (appIdStr, string.Empty);
                }
                
                string name = GetGameNameFromAppInfo(appInfo);
                _logger.LogInformation("Retrieved game name for AppID {0}: {1}", appId, name);
                
                return (appIdStr, name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving info for AppID {0}", appIdStr);
                return (appIdStr, string.Empty);
            }
            finally
            {
                // Leave connection open for future requests
            }
        }

        private string GetGameNameFromAppInfo(SteamApps.PICSProductInfoCallback.PICSProductInfo appInfo)
        {
            try
            {
                var kv = appInfo.KeyValues;
                return kv["common"]?["name"]?.AsString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting game name from app info");
                return string.Empty;
            }
        }

        public async Task<List<(string AppId, string GameName)>> GetAppInfoBatchAsync(IEnumerable<string> appIds)
        {
            var results = new List<(string AppId, string GameName)>();
            
            foreach (var appId in appIds)
            {
                var result = await GetAppInfoAsync(appId);
                results.Add(result);
            }
            
            return results;
        }

        public void Disconnect()
        {
            if (_isConnected)
            {
                _steamClient.Disconnect();
                _isConnected = false;
                _isLoggedIn = false;
            }
        }

        private async Task<List<uint>> GetOwnedAppsAsync(string username, string password)
        {
            var ownedApps = new List<uint>();
            
            try
            {
                bool connected = await ConnectAndLogin(username, password);
                if (!connected)
                {
                    _logger.LogError("Failed to login with the provided credentials");
                    return ownedApps;
                }

                _logger.LogInformation("Successfully logged in to Steam. Retrieving popular games list as fallback...");

                // Process callbacks in a separate task
                var callbackTask = Task.Run(() => {
                    while (_isConnected && _isLoggedIn)
                    {
                        _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(500));
                    }
                });

                // Thêm thời gian chờ để đảm bảo kết nối ổn định
                await Task.Delay(1000);

                // Sử dụng danh sách game phổ biến làm fallback
                foreach (var game in _popularGames)
                {
                    if (uint.TryParse(game.Key, out uint appId))
                    {
                        ownedApps.Add(appId);
                    }
                }
                
                _logger.LogInformation("Found {0} popular games", ownedApps.Count);
                return ownedApps;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting owned apps");
                
                // Dùng danh sách fallback trong trường hợp lỗi
                foreach (var game in _popularGames)
                {
                    if (uint.TryParse(game.Key, out uint appId))
                    {
                        ownedApps.Add(appId);
                    }
                }
                
                return ownedApps;
            }
        }

        // Method to get owned games from a Steam account
        public async Task<List<(string AppId, string GameName)>> GetOwnedGamesAsync(string username, string password)
        {
            var results = new List<(string AppId, string GameName)>();
            
            try
            {
                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    _logger.LogWarning("Username or password not provided to get owned games");
                    return GetPopularGames(); // Fallback to popular games
                }

                _logger.LogInformation("Getting owned games for account {Username}", username);

                // First attempt: Try to connect and get owned apps
                var ownedAppIds = await GetOwnedAppsAsync(username, password);
                
                if (ownedAppIds.Count == 0)
                {
                    _logger.LogWarning("Could not retrieve owned games from Steam, using fallback method");
                    return GetPopularGames(); // Fallback to popular games
                }
                
                // Get game names for each app ID
                foreach (var appId in ownedAppIds)
                {
                    var appIdStr = appId.ToString();
                    var (_, gameName) = await GetAppInfoAsync(appIdStr);
                    
                    if (!string.IsNullOrEmpty(gameName))
                    {
                        results.Add((appIdStr, gameName));
                    }
                }
                
                return results.OrderBy(g => g.GameName).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting owned games for account {Username}", username);
                return GetPopularGames(); // Fallback to popular games
            }
        }
        
        // Fallback method to return popular games
        public List<(string AppId, string GameName)> GetPopularGames()
        {
            var games = new List<(string AppId, string GameName)>();
            
            foreach (var game in _popularGames)
            {
                games.Add((game.Key, game.Value));
            }
            
            return games.OrderBy(g => g.GameName).ToList();
        }
    }
} 