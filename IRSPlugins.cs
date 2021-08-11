using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust;
using Newtonsoft.Json;
using ProtoBuf;

namespace Oxide.Plugins
{
    [Info("IRSPlugins", "Icrus", "0.1.0")]
    [Description("Private server plugin package")]
    public class IRSPlugins : RustPlugin
    {
        #region Classes
        [ProtoContract]
        public class IRSServer
        {
            [ProtoMember(1)]
            public Dictionary<UInt64, IRSBuildingBlock> BuildingBlocks;
        }
        [ProtoContract]
        public class IRSBuildingBlock
        {
            [ProtoMember(1)]
            public Int64 BuildingId;

            [ProtoMember(2)]
            public Int64 CreatedTimestamp;
        }
        public class IRSUser
        {
            public BasePlayer Player { private set; get; }
            public UInt64 Id { get { return Player.userID; } }
            public String IdString { get { return Player.UserIDString; } }
            public String Language { get { return Player.net.connection.info.GetString("global.language") ?? "en"; } }

            public Boolean IsAuthenticated = false;
            public Int32 AuthenticationRetryCount = 0;
            public Timer AuthenticationTimer = null;

            public Boolean IsDemolishModeEnabled = false;

            public IRSUser(BasePlayer player)
            {
                Player = player;
            }
        }
        public class IRSUserBehavior : UnityEngine.MonoBehaviour, IDisposable
        {
            public BasePlayer Player = null;
            public Single LastUpdatedTime = 0;

            private void Awake()
            {
                Player = GetComponent<BasePlayer>();
            }
            private void FixedUpdate()
            {
                if (Player == null || !Player.IsConnected)
                {
                    Dispose();
                }
                else
                {
                    #region old
                    ////var time = UnityEngine.Time.realtimeSinceStartup;
                    ////if (time - LastUpdatedTime >= IRSPlugins.Me._behavior_event_delay)
                    ////{

                    ////}

                    //// Hammer + Right click
                    //if (Player.GetActiveItem().info.shortname == "hammer" &&
                    //    Player.serverInput.WasJustPressed(BUTTON.FIRE_SECONDARY))
                    //{
                    //    IRSPlugins.Me.OnHammerRightClick(Player);
                    //}

                    //// Update last updated time
                    //// LastUpdatedTime = time;
                    #endregion

                    // Retrieve item data
                    var item = Player.GetActiveItem();

                    // Check state
                    if (item != null)
                    {
                        // Hammer + Right click
                        if (Player.GetActiveItem().info.shortname == "hammer" && Player.serverInput.WasJustPressed(BUTTON.FIRE_SECONDARY))
                        {
                            IRSPlugins.Me.OnHammerRightClick(Player);
                        }
                    }
                }
            }
            public void Dispose()
            {
                Destroy(this);
            }
        }
        #endregion

        #region Variables

        // Commons
        public static IRSPlugins Me = null;
        private IRSServer _server;
        private List<IRSUser> _users;

        // Behavior
        private Single _max_distance;
        private Int32 _layer_mask;

        // Authentication
        private String _message_password_mask;

        #endregion

        #region Hooks
        private void Init()
        {
            LoadDefaultConfig();
            LoadDefaultMessages();
            LoadDefaultServerData();

            // Commons
            Me = this;
            _users = new List<IRSUser>();

            // Behavior
            _max_distance = 200f;
            _layer_mask = UnityEngine.LayerMask.GetMask("Construction", "Deployed", "Default");

            // Authentication
            Int32 length = Config["AuthPassword"].ToString().Length;
            _message_password_mask = new StringBuilder(length).Insert(0, "*", length).ToString();
        }
        private void Unload()
        {
            // Remove user behavior component
            foreach (var i in UnityEngine.Object.FindObjectsOfType<IRSUserBehavior>())
            {
                UnityEngine.GameObject.Destroy(i);
            }

            // Save server data
            SaveServerData();
        }
        private void OnServerInitialized()
        {
            // Player relative process
            foreach (var i in BasePlayer.activePlayerList)
            {
                OnPlayerConnected(i);
            }

            // Entity relative process
            Int32 time_sec;
            Int64 time_diff;
            foreach (var i in BaseEntity.saveList)
            {
                // Check building block
                if (i != null && i is BuildingBlock)
                {
                    // Retrieve building block
                    var block = i as BuildingBlock;

                    // Register building block
                    OnEntitySpawned(block);

                    // Set demolishable state
                    time_sec = Convert.ToInt32(Config["BuildDemolishableTimeSec"]);
                    if (time_sec < 0)
                    {
                        SetDemolishableTime(block, time_sec);
                    }
                    else
                    {
                        time_diff = time_sec - (GetCurrentTimestamp() - _server.BuildingBlocks[block.net.ID].CreatedTimestamp);
                        if (time_diff > 0)
                        {
                            SetDemolishableTime(block, time_diff);
                        }
                        else
                        {
                            block.StopBeingDemolishable();
                        }
                    }

                    // Set rotatable state
                    if (block.blockDefinition.canRotateAfterPlacement)
                    {
                        time_sec = Convert.ToInt32(Config["BuildRotatableTimeSec"]);
                        if (time_sec < 0)
                        {
                            SetRotatableTime(block, time_sec);
                        }
                        else
                        {
                            time_diff = time_sec - (GetCurrentTimestamp() - _server.BuildingBlocks[block.net.ID].CreatedTimestamp);
                            if (time_diff > 0)
                            {
                                SetRotatableTime(block, time_diff);
                            }
                            else
                            {
                                block.StopBeingRotatable();
                            }
                        }
                    }
                }
            }
        }
        private void OnPlayerConnected(BasePlayer player)
        {
            // Register user object
            if (!_users.Exists((x) => x.Id == player.userID))
            {
                _users.Add(new IRSUser(player));
            }

            // Authentication
            var user = _users.FirstOrDefault((x) => x.Id == player.userID);
            if ((Boolean)Config["AuthEnabled"])
            {
                AuthProcess(user);
            }

            // Add user behavior component
            if (user.Player.gameObject.GetComponent<IRSUserBehavior>() == null)
            {
                user.Player.gameObject.AddComponent<IRSUserBehavior>();
            }
        }
        private void OnPlayerDisconnected(BasePlayer player)
        {
            var user = _users.FirstOrDefault((x) => x.Id == player.userID);
            if (user != null)
            {
                // Remove user behavior component
                user.Player.gameObject.GetComponent<IRSUserBehavior>()?.Dispose();

                // Remove user object
                _users.Remove(user);
            }
        }
        private Object OnPlayerChat(BasePlayer player, String message)
        {
            // Retrieve user
            var user = _users.FirstOrDefault((x) => x.Id == player.userID);

            // Check user auth
            if ((Boolean)Config["AuthEnabled"])
            {
                if (user != null && !user.IsAuthenticated)
                {
                    ShowMessage(player, "AuthChatForbidden");
                    return true;
                }

                // Check password string
                if (message.Contains(Config["AuthPassword"].ToString()))
                {
                    String message_replaced = message.Replace(Config["AuthPassword"].ToString(), _message_password_mask);
                    rust.BroadcastChat($"<color=#5af>{player.displayName}</color>", message_replaced, player.UserIDString);
                    return true;
                }
            }

            return null;
        }
        private void OnEntitySpawned(BaseNetworkable entity)
        {
            if (entity != null && entity is BuildingBlock)
            {
                // Retrieve building block
                var block = entity as BuildingBlock;

                // Register building block
                if (!_server.BuildingBlocks.ContainsKey(block.net.ID))
                {
                    _server.BuildingBlocks.Add(block.net.ID, new IRSBuildingBlock()
                    {
                        BuildingId = block.buildingID,
                        CreatedTimestamp = GetCurrentTimestamp(),
                    });
                }
            }
        }
        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity != null && entity is BuildingBlock)
            {
                // Retrieve building block
                var block = entity as BuildingBlock;

                // Unregister building block
                _server.BuildingBlocks.Remove(block.net.ID);
            }
        }
        #endregion

        #region InternalEvents
        // 테스트 중 함수
        private void OnHammerRightClick(BasePlayer player)
        {
           
            var entity = GetHeadEntity(player);
            if (entity is BuildingBlock)
            {
                // Retrieve building block
                var block = entity as BuildingBlock;

                // Retrieve protected time
                PrintToChatAdmin(player, "Id : {0}", block.net.ID);
            }
        }
        #endregion

        #region ChatCommands
        [ChatCommand("auth")]
        private void TryAuthentication(BasePlayer player, String command, String[] args)
        {
            // Check authentication enabled
            if (!(Boolean)Config["AuthEnabled"])
            {
                return;
            }

            // Retrieve user
            var user = _users.FirstOrDefault((x) => x.Id == player.userID);

            // Check password
            if (user != null && !user.IsAuthenticated)
            {
                if (args.Length == 0)
                {
                    // Show invalid syntax message
                    ShowMessage(player, "AuthInvalid");
                }
                else
                {
                    if (args[0] == Config["AuthPassword"].ToString())
                    {
                        // Authentication success
                        user.AuthenticationTimer.Destroy();
                        user.AuthenticationRetryCount = 0;
                        user.IsAuthenticated = true;

                        // Register white list
                        if ((Boolean)Config["AuthEnableAutoRegisteration"])
                        {
                            ((List<Object>)Config["AuthWhitelist"]).Add(user.Id);
                            SaveConfig();

                            ShowMessage(player, "AuthRegSuccess");
                        }
                        else
                        {
                            ShowMessage(player, "AuthSuccess");
                        }
                    }
                    else
                    {
                        // Authentication fail
                        ++user.AuthenticationRetryCount;

                        // Check retry count
                        if (user.AuthenticationRetryCount >= (Int32)Config["AuthMaxRetryCount"])
                        {
                            player.Kick(lang.GetMessage("AuthFailure", this, player.UserIDString));
                        }
                        else
                        {
                            ShowMessage(player, "AuthIncorrectPassword", (Int32)Config["AuthMaxRetryCount"] - user.AuthenticationRetryCount);
                        }
                    }
                }
            }
        }
        #endregion

        #region ConsoleCommands

        #endregion

        #region Helpers
        private Boolean IsAdmin(BasePlayer player)
        {
            return player.net.connection.authLevel == 2;
        }
        private Int64 GetCurrentTimestamp()
        {
            return DateTime.Now.Ticks / 10000000;
        }
        private void AuthProcess(IRSUser user)
        {
            // Check is authenticated
            if (user.IsAuthenticated)
            {
                return;
            }

            // Check authentication white list
            if (((List<Object>)Config["AuthWhitelist"]).Contains(user.IdString))
            {
                ShowMessage(user.Player, "AuthWhitelist");
                user.IsAuthenticated = true;
                return;
            }

            // Show notice, Activate kick timer
            ShowMessage(user.Player, "Auth", Config["AuthTimeout"]);
            user.AuthenticationTimer = timer.Once((Int32)Config["AuthTimeout"], () =>
            {
                user.Player.Kick(lang.GetMessage("AuthTimeout", this));
            });
        }
        private void SetDemolishableTime(BuildingBlock block, Single seconds)
        {
            if (seconds == 0f)
            {
                block.StopBeingDemolishable();
            }
            else
            {
                block.CancelInvoke(block.StopBeingDemolishable);
                block.SetFlag(BaseEntity.Flags.Reserved2, true, false); // Reserved2 : Demolishable
                if (seconds > 0f)
                {
                    block.Invoke(block.StopBeingDemolishable, seconds);
                }
            }
        }
        private void SetRotatableTime(BuildingBlock block, Single seconds)
        {
            if (seconds == 0f)
            {
                block.StopBeingRotatable();
            }
            else
            {
                block.CancelInvoke(block.StopBeingRotatable);
                block.SetFlag(BaseEntity.Flags.Reserved1, true, false); // Reserved1 : Rotatable
                if (seconds > 0f)
                {
                    block.Invoke(block.StopBeingRotatable, seconds);
                }
            }
        }
        private BaseEntity GetHeadEntity(BasePlayer player)
        {
            UnityEngine.RaycastHit hit;
            return UnityEngine.Physics.Raycast(player.eyes.HeadRay(), out hit, _max_distance, _layer_mask) ? hit.GetEntity() : null;
        }

        protected override void LoadDefaultConfig()
        {
            // Authentication
            Config["AuthEnabled"] = Config["AuthEnabled"] ?? true;
            Config["AuthPassword"] = Config["AuthPassword"] ?? "0000";
            Config["AuthMaxRetryCount"] = Config["AuthMaxRetryCount"] ?? 5;
            Config["AuthTimeout"] = Config["AuthTimeout"] ?? 30;
            Config["AuthDisableChat"] = Config["AuthDisableChat"] ?? true;
            Config["AuthEnableAutoRegisteration"] = Config["AuthEnableAutoRegisteration"] ?? true;
            Config["AuthWhitelist"] = Config["AuthWhitelist"] ?? new List<Object>();

            // Building
            Config["BuildDemolishableTimeSec"] = Config["BuildDemolishableTimeSec"] ?? 600f; // Default : 600
            Config["BuildRotatableTimeSec"] = Config["BuildRotatableTimeSec"] ?? 600f; // Default : 600

            SaveConfig();
        }
        protected override void LoadDefaultMessages()
        {
            // Register english messages
            lang.RegisterMessages(new Dictionary<String, String>
            {
                ["Auth"] = "Type '/auth [Password]' in the following {0} seconds to authenticate",
                ["AuthInvalid"] = "Invalid syntax. Type '/auth [Password]'.",
                ["AuthIncorrectPassword"] = "Incorrect password. You have {0} retries left.",
                ["AuthTimeout"] = "You took too long to authenticate",
                ["AuthFailure"] = "You exceeded the maximum amout of retries",
                ["AuthSuccess"] = "Authentication successful.",
                ["AuthRegSuccess"] = "Authentication and registeration in whitelist successfully.",
                ["AuthWhitelist"] = "Authentication successful (Whitelist user)",
                ["AuthChatForbidden"] = "You can't chat before authentication.",
            }, this, "en");

            // Register korean messages
            lang.RegisterMessages(new Dictionary<String, String>
            {
                ["Auth"] = "인증을 위해 {0}초 내로 '/auth [패스워드]' 형태로 패스워드를 입력해주세요.",
                ["AuthInvalid"] = "잘못된 입력입니다. '/auth [패스워드]' 형태로 패스워드를 입력해주세요.",
                ["AuthIncorrectPassword"] = "잘못된 패스워드입니다. 입력 가능 횟수가 {0}회 남았습니다.",
                ["AuthTimeout"] = "패스워드 입력 시간이 초과되었습니다.",
                ["AuthFailure"] = "패스워드 입력 가능 횟수를 초과하였습니다.",
                ["AuthSuccess"] = "인증에 성공하였습니다.",
                ["AuthRegSuccess"] = "인증에 성공 및 화이트리스트에 등록이 완료되었습니다.",
                ["AuthWhitelist"] = "자동으로 인증되었습니다. (화이트리스트 유저)",
                ["AuthChatForbidden"] = "인증 전까지 채팅을 칠 수 없습니다.",
            }, this, "ko");
        }
        private void LoadDefaultServerData()
        {
            if (!ProtoStorage.Exists("IRSServer"))
            {
                // Create default server data and save
                _server = new IRSServer();
                _server.BuildingBlocks = new Dictionary<UInt64, IRSBuildingBlock>();

                SaveServerData();
            }
            else
            {
                _server = ProtoStorage.Load<IRSServer>("IRSServer");
                if (_server.BuildingBlocks == null)
                {
                    _server.BuildingBlocks = new Dictionary<UInt64, IRSBuildingBlock>();
                }
            }
        }
        private void SaveServerData()
        {
            ProtoStorage.Save(_server, "IRSServer");
        }
        private void ShowMessage(BasePlayer player, String key, params Object[] args)
        {
            PrintToChat(player, lang.GetMessage(key, this, player.UserIDString), args);
        }
        private void PrintToChatAdmin(BasePlayer player, String format, params Object[] args)
        {
            if (IsAdmin(player))
            {
                PrintToChat(player, format, args);
            }
        }
        private void PrintToConsoleAdmin(BasePlayer player, String format, params Object[] args)
        {
            if (IsAdmin(player))
            {
                PrintToConsole(player, format, args);
            }
        }
        #endregion
    }
}
