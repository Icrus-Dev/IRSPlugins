using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;

using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;

using ProtoBuf;

/*  
    [Basic Information For Custom UI]

    1) Anchor, Offset 설정 (0 ~ 1)
    Left    Bottom   (Min)
    Right   Top      (Max)

    2) Offset을 통해 개체의 길이를 조절해볼 수 있다.
*/

namespace Oxide.Plugins
{
    [Info("IRSPlugins", "Icrus", "0.2.0")]
    [Description("Private server plugin package")]
    public class IRSPlugins : RustPlugin
    {
        #region Classes
        public class IRSUser
        {
            // Commons
            public BasePlayer Player { private set; get; }
            public UInt64 Id { get { return Player.userID; } }
            public String IdString { get { return Player.UserIDString; } }
            public String Language { get { return Player.net.connection.info.GetString("global.language") ?? "en"; } }

            // User Data
            public IRSUserData UserData;

            // Authentication
            public Boolean IsAuthenticated = false;
            public Int32 AuthenticationRetryCount = 0;
            public Timer AuthenticationTimer = null;

            // Custom UIs
            public ItemContainer SkinContainer = null;
            public Boolean IsSkinPanelVisible = false;
            public Boolean IsSkinPanelUpdating = false;
            public Boolean IsSkinHammerMode = false;
            public Item SkinTargetItem = null;
            public BaseEntity SkinTargetEntity = null;
            public Int32 SkinPanelPage = 1;

            // Constructor
            public IRSUser(BasePlayer player)
            {
                Player = player;
            }
        }

        [ProtoContract]
        public class IRSUserData
        {
            // Default skins
            public Dictionary<UInt64, UInt64> DefaultSkins = new Dictionary<UInt64, UInt64>();
        }

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

        public class IRSUserBehavior_ : UnityEngine.MonoBehaviour, IDisposable
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
        public static IRSPlugins Me;
        private IRSServer _server;
        private Dictionary<UInt64, IRSUser> _users;

        // Head entity config
        private Single _max_distance;
        private Int32 _layer_mask;

        // Authentication
        private String _message_password_mask;

        // Custom UI
        private IEnumerable<IGrouping<Int32?, UInt64>> _skins;
        private String _skin_container_ui;
        private Int32 _skin_container_page_position;

        #endregion

        #region Hooks
        private void Init()
        {
            Me = this;

            // Load default data
            LoadDefaultConfig();
            LoadDefaultMessages();
            LoadDefaultServerData();

            // Commons
            _users = new Dictionary<UInt64, IRSUser>();

            // Head entity config
            _max_distance = 200f;
            _layer_mask = UnityEngine.LayerMask.GetMask("Construction", "Deployed", "Default");

            // Authentication
            if (IsAuthEnabled())
            {
                Int32 length = Config["AuthPassword"].ToString().Length;
                _message_password_mask = new StringBuilder(length).Insert(0, "*", length).ToString();
            }
            else
            {
                _message_password_mask = String.Empty;
            }

            if (IsSkinEnabled())
            {
                // Create skin cache
                _skins = Rust.Workshop.Approved.All
                    .GroupBy(x => ItemManager.itemList.FirstOrDefault(y => y.shortname == x.Value.Skinnable.ItemName)?.itemid, x => x.Value.WorkshopdId);

                // Create skin container ui
                var ui_container = new CuiElementContainer();
                ui_container.Add(new CuiElement()
                {
                    Name = "SkinContainerPanel",
                    Parent = "Overlay",
                    Components =
                {
                    new CuiImageComponent()
                    {
                        Color = "0 0 0 0.5",
                    },
                    new CuiRectTransformComponent()
                    {
                        AnchorMin = "1 0",
                        AnchorMax = "1 0",
                        OffsetMin = "-442 16",
                        OffsetMax = "-220 98",
                    }
                },
                    FadeOut = 0f,
                });
                ui_container.Add(new CuiElement()
                {
                    Name = "SkinContainerPrevious",
                    Parent = "SkinContainerPanel",
                    Components =
                {
                    new CuiButtonComponent()
                    {
                        Color = "0 0 0 0",
                        Command = $"_skin_prev",
                        Close = "SkinContainerPanel",
                    },
                    new CuiRectTransformComponent()
                    {
                        AnchorMin = "0.025 0.05",
                        AnchorMax = "0.225 0.95",
                    }
                },
                    FadeOut = 0f,
                });
                ui_container.Add(new CuiElement()
                {
                    Name = "SkinContainerPreviousText",
                    Parent = "SkinContainerPrevious",
                    Components =
                {
                    new CuiTextComponent()
                    {
                        Text = "<size=36><</size>",
                        Align = UnityEngine.TextAnchor.MiddleCenter,
                        Color = "1 1 1 0.75",
                    },
                    new CuiRectTransformComponent()
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                    }
                },
                    FadeOut = 0f,
                });
                ui_container.Add(new CuiElement()
                {
                    Name = "SkinContainerPage",
                    Parent = "SkinContainerPanel",
                    Components =
                {
                    new CuiImageComponent()
                    {
                        Color = "0 0 0 0",
                    },
                    new CuiRectTransformComponent()
                    {
                        AnchorMin = "0.250 0.05",
                        AnchorMax = "0.750 0.95",
                    }
                },
                    FadeOut = 0f,
                });
                ui_container.Add(new CuiElement()
                {
                    Name = "SkinContainerPageText",
                    Parent = "SkinContainerPage",
                    Components =
                {
                    new CuiTextComponent()
                    {
                        Text = "<size=24>{0}</size>",
                        Align = UnityEngine.TextAnchor.MiddleCenter,
                        Color = "1 1 1 0.75",
                    },
                    new CuiRectTransformComponent()
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                    }
                }
                });
                ui_container.Add(new CuiElement()
                {
                    Name = "SkinContainerNext",
                    Parent = "SkinContainerPanel",
                    Components =
                {
                    new CuiButtonComponent()
                    {
                        Color = "0 0 0 0",
                        Command = $"_skin_next",
                        Close = "SkinContainerPanel",
                    },
                    new CuiRectTransformComponent()
                    {
                        AnchorMin = "0.775 0.05",
                        AnchorMax = "0.975 0.95",
                    }
                },
                    FadeOut = 0.5f,
                });
                ui_container.Add(new CuiElement()
                {
                    Name = "SkinContainerNextText",
                    Parent = "SkinContainerNext",
                    Components =
                {
                    new CuiTextComponent()
                    {
                        Text = "<size=36>></size>",
                        Align = UnityEngine.TextAnchor.MiddleCenter,
                        Color = "1 1 1 0.75",
                    },
                    new CuiRectTransformComponent()
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                    }
                },
                    FadeOut = 0f,
                });
                _skin_container_ui = ui_container.ToJson();
                _skin_container_page_position = _skin_container_ui.IndexOf("{0}");
                _skin_container_ui = _skin_container_ui.Remove(_skin_container_page_position, 3);

                // Register skin container ui commands
                AddCovalenceCommand("_skin_prev", nameof(TryLoadSkinContainerPreviousPage));
                AddCovalenceCommand("_skin_next", nameof(TryLoadSkinContainerNextPage));
            }

        }
        private void Unload()
        {
            // Remove user behavior component
            //foreach (var i in UnityEngine.Object.FindObjectsOfType<IRSUserBehavior>())
            //{
            //    UnityEngine.GameObject.Destroy(i);
            //}

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
            if (!_users.ContainsKey(player.userID))
            {
                _users.Add(player.userID, new IRSUser(player));
            }

            // Retrieve user data
            if (!ProtoStorage.Exists("users", player.UserIDString))
            {
                _users[player.userID].UserData = new IRSUserData();
                ProtoStorage.Save(_users[player.userID].UserData, "IRSUserData", player.UserIDString);
            }
            else
            {
                _users[player.userID].UserData = ProtoStorage.Load<IRSUserData>("IRSUserData", player.UserIDString);
            }

            // Authentication
            if (IsAuthEnabled())
            {
                AuthProcess(player.userID);
            }

            // Create skin container
            if (IsSkinEnabled())
            {
                _users[player.userID].SkinContainer = new ItemContainer();
                _users[player.userID].SkinContainer.entityOwner = player;
                _users[player.userID].SkinContainer.capacity = 42;
                _users[player.userID].SkinContainer.isServer = true;
                _users[player.userID].SkinContainer.allowedContents = ItemContainer.ContentsType.Generic;
                _users[player.userID].SkinContainer.GiveUID();
            }

            // Add user behavior component
            //if (user.Player.gameObject.GetComponent<IRSUserBehavior>() == null)
            //{
            //    user.Player.gameObject.AddComponent<IRSUserBehavior>();
            //}
        }
        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (_users.ContainsKey(player.userID))
            {
                // Remove user behavior component
                //user.Player.gameObject.GetComponent<IRSUserBehavior>()?.Dispose();

                // Save user data
                ProtoStorage.Save(_users[player.userID].UserData, "IRSUserData", player.UserIDString);

                // Remove user object
                _users.Remove(player.userID);
            }
        }
        private Object OnPlayerChat(BasePlayer player, String message)
        {
            // Check user auth
            if (player != null && IsAuthEnabled())
            {
                if (_users.ContainsKey(player.userID) && !_users[player.userID].IsAuthenticated)
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
        private Object OnServerMessage(String message, String name)
        {
            // Ignore give message
            if (IsIgnoreGiveMessageEnabled() && name == "SERVER" && message.Contains("gave themselves"))
            {
                var player_name = message.Split(new String[] { "gave" }, StringSplitOptions.None).FirstOrDefault()?.Trim();
                if (!String.IsNullOrWhiteSpace(player_name))
                {
                    var player = BasePlayer.activePlayerList.First((x) => x.displayName == player_name);
                    if (player != null)
                    {
                        if ((Boolean)Config["IgnoreGiveMessageAdmin"] && IsAdmin(player))
                        {
                            return true;
                        }
                        if (((List<Object>)Config["IgnoreGiveMessagePlayers"]).Contains(player.userID))
                        {
                            return true;
                        }
                    }
                }
            }

            return null;
        }
        private void OnEntitySpawned(BaseNetworkable entity)
        {
            if (entity != null && entity.net != null &&  entity is BuildingBlock)
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
            if (entity != null && entity.net != null && entity is BuildingBlock)
            {
                // Retrieve building block
                var block = entity as BuildingBlock;

                // Unregister building block
                _server.BuildingBlocks.Remove(block.net.ID);
            }
        }
        private object CanMoveItem(Item item, PlayerInventory inventory, uint target_container, int target_slot, int amount)
        {
            // Skin load using right click, drag
            if (item != null && inventory != null)
            {
                var player = inventory._baseEntity;
                if (player != null && _users.ContainsKey(player.userID) && _users[player.userID].IsSkinPanelVisible)
                {
                    if (_users[player.userID].IsSkinHammerMode)
                    {
                        // Ignore skin container update
                        if (target_container == _users[player.userID].SkinContainer.uid)
                        {
                            return true;
                        }

                        // From skin container
                        ItemContainer container = item.GetRootContainer();
                        if (container == null)
                        {
                            // Apply skin on entity
                            _users[player.userID].SkinTargetEntity.skinID = item.skin;
                            _users[player.userID].SkinTargetEntity.SendNetworkUpdateImmediate();

                            // Close skin container ui
                            CloseSkinContainerUI(player);

                            // Ignore move item
                            return true;
                        }
                    }
                    else
                    {
                        // Player container to skin container
                        if (target_container == _users[player.userID].SkinContainer.uid)
                        {
                            // Update skin container contents
                            UpdateSkinContainer(player, item, 1);

                            // Ignore move item
                            return true;
                        }
                        else
                        {
                            // Skin container to player container
                            ItemContainer container = item.GetRootContainer();
                            if (container == null)
                            {
                                // Apply skin on item
                                _users[player.userID].SkinTargetItem.skin = item.skin;
                                _users[player.userID].SkinTargetItem.MarkDirty();
                                if (_users[player.userID].SkinTargetItem.info.itemMods != null)
                                {
                                    foreach (var i in _users[player.userID].SkinTargetItem.info.itemMods)
                                    {
                                        if (i != null)
                                        {
                                            i.OnParentChanged(_users[player.userID].SkinTargetItem);
                                        }
                                    }
                                }

                                // Apply skin on entity
                                BaseEntity entity = _users[player.userID].SkinTargetItem.GetHeldEntity();
                                if (entity != null)
                                {
                                    entity.skinID = item.skin;
                                    entity.SendNetworkUpdateImmediate();
                                }

                                // Ignore move item
                                return true;
                            }
                        }
                    }
                }
            }

            return null;
        }
        private Object CanLootPlayer(BasePlayer target, BasePlayer looter)
        {
            // For custom UI
            if (target == looter)
            {
                return true;
            }

            return null;
        }
        private void OnPlayerLootEnd(PlayerLoot loot)
        {
            // Close skin container
            if (loot != null && loot.entitySource != null && loot.entitySource is BasePlayer)
            {
                var player = loot.entitySource as BasePlayer;
                if (_users.ContainsKey(player.userID) && _users[player.userID].IsSkinPanelVisible && !_users[player.userID].IsSkinPanelUpdating)
                {
                    CloseSkinContainerUI(player);
                }
            }
        }
        private object OnHammerHit(BasePlayer player, HitInfo info)
        {
            // Open skin container ui using hammer
            if (_users.ContainsKey(player.userID) && info.HitEntity != null && info.HitEntity.Health() == info.HitEntity.MaxHealth())
            {
                var item = ItemManager.CreateByName(info.HitEntity.ShortPrefabName);
                if (item != null)
                {
                    // Set hammer mode flag
                    _users[player.userID].IsSkinHammerMode = true;

                    // Set target entity
                    _users[player.userID].SkinTargetEntity = info.HitEntity;

                    // Open skin container ui
                    OpenSkinContainerUI(player, item);
                }

                return true;
            }

            return null;
        }
        #endregion

        #region InternalEvents

        #endregion

        #region ChatCommands
        [ChatCommand("auth")]
        private void TryAuthentication(BasePlayer player, String command, String[] args)
        {
            // Check authentication enabled
            if (!IsAuthEnabled())
            {
                return;
            }

            // Check password
            if (_users.ContainsKey(player.userID) && !_users[player.userID].IsAuthenticated)
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
                        _users[player.userID].AuthenticationTimer.Destroy();
                        _users[player.userID].AuthenticationRetryCount = 0;
                        _users[player.userID].IsAuthenticated = true;

                        // Register white list
                        if ((Boolean)Config["AuthEnableAutoRegisteration"])
                        {
                            ((List<Object>)Config["AuthWhitelist"]).Add(player.userID);
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
                        ++_users[player.userID].AuthenticationRetryCount;

                        // Check retry count
                        if (_users[player.userID].AuthenticationRetryCount >= (Int32)Config["AuthMaxRetryCount"])
                        {
                            player.Kick(lang.GetMessage("AuthFailure", this, player.UserIDString));
                        }
                        else
                        {
                            ShowMessage(player, "AuthIncorrectPassword", (Int32)Config["AuthMaxRetryCount"] - _users[player.userID].AuthenticationRetryCount);
                        }
                    }
                }
            }
        }
        [ChatCommand("skin")]
        private void TryOpenSkinUi(BasePlayer player, String command, String[] args)
        {
            if (IsSkinEnabled())
            {
                OpenSkinContainerUI(player);
            }
        }
        #endregion

        #region ConsoleCommands

        #endregion

        #region CustomCommands
        private void TryLoadSkinContainerNextPage(IPlayer player_interface, String command, String[] args)
        {
            var player = player_interface.Object as BasePlayer;
            if (player != null && _users.ContainsKey(player.userID))
            {
                UpdateSkinContainer(player, _users[player.userID].SkinTargetItem, _users[player.userID].SkinPanelPage + 1);
            }
        }
        private void TryLoadSkinContainerPreviousPage(IPlayer player_interface, String command, String[] args)
        {
            var player = player_interface.Object as BasePlayer;
            if (player != null && _users.ContainsKey(player.userID))
            {
                UpdateSkinContainer(player, _users[player.userID].SkinTargetItem, _users[player.userID].SkinPanelPage - 1);
            }
        }
        #endregion

        #region Helpers
        private Boolean IsAdmin(BasePlayer player)
        {
            return player.net.connection.authLevel == 2;
        }
        private Boolean IsAuthEnabled()
        {
            return !String.IsNullOrEmpty((String)Config["AuthPassword"]);
        }
        private Boolean IsIgnoreGiveMessageEnabled()
        {
            return (Boolean)Config["IgnoreGiveMessageEnabled"];
        }
        private Boolean IsSkinEnabled()
        {
            return (Boolean)Config["SkinEnabled"];
        }

        private Int64 GetCurrentTimestamp()
        {
            return DateTime.Now.Ticks / 10000000;
        }

        private void AuthProcess(UInt64 player_id)
        {
            // Check is authenticated
            if (_users[player_id].IsAuthenticated)
            {
                return;
            }

            // Check authentication white list
            if (((List<Object>)Config["AuthWhitelist"]).Contains(_users[player_id].IdString))
            {
                // ShowMessage(user.Player, "AuthWhitelist");
                _users[player_id].IsAuthenticated = true;
                return;
            }

            // Show notice, Activate kick timer
            ShowMessage(_users[player_id].Player, "Auth", Config["AuthTimeout"]);
            _users[player_id].AuthenticationTimer = timer.Once((Int32)Config["AuthTimeout"], () =>
            {
                _users[player_id].Player.Kick(lang.GetMessage("AuthTimeout", this));
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

        private void OpenSkinContainerUI(BasePlayer player, Item item = null)
        {
            if (_users.ContainsKey(player.userID) && !_users[player.userID].IsSkinPanelVisible)
            {
                player.Invoke(() =>
                {
                    // Prepare empty skin container
                    UpdateSkinContainer(player, item);

                    // Send RPC_OpenLootPanel
                    player.ClientRPCPlayer(null, player, "RPC_OpenLootPanel", "GenericLarge"); // Generic : 36, GenericLarge : 42

                    if (_users.ContainsKey(player.userID))
                    {
                        // Set skin container visibility
                        _users[player.userID].IsSkinPanelVisible = true;

                        // Reset skin container page
                        _users[player.userID].SkinPanelPage = 1;
                    }

                }, 0.25f);
            }
        }
        private void CloseSkinContainerUI(BasePlayer player)
        {
            if (_users.ContainsKey(player.userID))
            {
                // Destroy skin container
                DestroySkinContainerUI(player);

                // Reset skin container page
                _users[player.userID].SkinPanelPage = 1;

                // Reset skin container target item
                _users[player.userID].SkinTargetItem = null;

                // Reset skin container target entity
                _users[player.userID].SkinTargetEntity = null;

                // Clear skin container
                _users[player.userID].SkinContainer.Clear();

                // Reset skin container hammer mode flag
                _users[player.userID].IsSkinHammerMode = false;
                
                // Reset skin container visibility
                _users[player.userID].IsSkinPanelVisible = false;
            }
        }
        private void DrawSkinContainerUI(BasePlayer player, Int32 page, Int32 page_max)
        {
            CuiHelper.AddUi(player, _skin_container_ui.Insert(_skin_container_page_position, $"{page} / {page_max}"));
        }
        private void DestroySkinContainerUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "SkinContainerPanel");
        }
        private void UpdateSkinContainer(BasePlayer player, Item item, Int32 page = 1)
        {
            // Validation
            if (!_users.ContainsKey(player.userID))
            {
                return;
            }

            // Set flag
            _users[player.userID].IsSkinPanelUpdating = true;

            // Update skin container
            Int32 page_max;
            IGrouping<Int32?, UInt64> item_skins;
            if (item == null || (item_skins = _skins.FirstOrDefault((x) => x.Key == item.info.itemid)) == null)
            {
                // Set max page
                page_max = 1;

                // Validate page
                if (page < 1)
                {
                    DrawSkinContainerUI(player, 1, page_max);
                    return;
                }
                else if (page_max < page)
                {
                    DrawSkinContainerUI(player, page_max, page_max);
                    return;
                }

                // Update target item
                _users[player.userID].SkinTargetItem = item;

                // Clear skin container
                _users[player.userID].SkinContainer.Clear();
            }
            else
            {
                // Retrieve max page
                page_max = (Int32)Math.Ceiling((Single)item_skins.Count() / _users[player.userID].SkinContainer.capacity);

                // Validate page
                if (page < 1)
                {
                    DrawSkinContainerUI(player, 1, page_max);
                    return;
                }
                else if (page_max < page)
                {
                    DrawSkinContainerUI(player, page_max, page_max);
                    return;
                }

                // Update container items
                IEnumerable<UInt64> skins;
                if (page == 1)
                {
                    // Retrieve skins
                    skins = item_skins.Take(_users[player.userID].SkinContainer.capacity - 1);
                }
                else
                {
                    // Retrieve skins
                    skins = item_skins.Skip(_users[player.userID].SkinContainer.capacity * (page - 1))
                                      .Take(_users[player.userID].SkinContainer.capacity);
                }

                // Update target item
                _users[player.userID].SkinTargetItem = item;

                // Clear skin container
                _users[player.userID].SkinContainer.Clear();

                // Add Default skin if page is 1
                if (page == 1)
                {
                    AddItemToContainer(DuplicateItem(item, 1, 0), _users[player.userID].SkinContainer);
                }

                // Add skins
                foreach (var i in skins)
                {
                    AddItemToContainer(DuplicateItem(item, 1, i), _users[player.userID].SkinContainer);
                }
            }

            // Update skin container page
            _users[player.userID].SkinPanelPage = page;

            // Clear player loot
            player.inventory.loot.Clear();

            // Update player loot
            player.inventory.loot.PositionChecks = false;
            player.inventory.loot.entitySource = player;
            player.inventory.loot.itemSource = null;
            player.inventory.loot.AddContainer(_users[player.userID].SkinContainer);
            player.inventory.loot.SendImmediate();

            // Update ui
            DestroySkinContainerUI(player);
            DrawSkinContainerUI(player, page, page_max);

            // Unset flag
            _users[player.userID].IsSkinPanelUpdating = false;
        }
        private Item DuplicateItem(Item item, Int32 amount = 0, UInt64 skin_id = 0)
        {
            var item_dup = ItemManager.Create(item.info, amount == 0 ? item.amount : amount, skin_id);

            // Copy condition state
            if (item.hasCondition)
            {
                item_dup.condition = item.condition;
                item_dup.maxCondition = item.maxCondition;
            }

            // Copy contents state
            if (item.contents != null)
            {
                item_dup.contents.capacity = item.contents.capacity;
            }

            // Copy primary magazine contents state if item is projectile
            var projectile = item.GetHeldEntity() as BaseProjectile;
            if (projectile != null)
            {
                (item_dup.GetHeldEntity() as BaseProjectile).primaryMagazine.contents = projectile.primaryMagazine.contents;
            }

            return item_dup;
        }
        private void AddItemToContainer(Item item, ItemContainer container)
        {
            // Add to container
            container.itemList.Add(item);

            // Mark dirty
            item.MarkDirty();

            // Call 'OnParentChanged'
            foreach (var i in item.info.itemMods)
            {
                i.OnParentChanged(item);
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
            Config["AuthPassword"] = Config["AuthPassword"] ?? null;
            Config["AuthMaxRetryCount"] = Config["AuthMaxRetryCount"] ?? 5;
            Config["AuthTimeout"] = Config["AuthTimeout"] ?? 30;
            Config["AuthDisableChat"] = Config["AuthDisableChat"] ?? true;
            Config["AuthEnableAutoRegisteration"] = Config["AuthEnableAutoRegisteration"] ?? true;
            Config["AuthWhitelist"] = Config["AuthWhitelist"] ?? new List<Object>();

            // Building
            Config["BuildDemolishableTimeSec"] = Config["BuildDemolishableTimeSec"] ?? 600f;
            Config["BuildRotatableTimeSec"] = Config["BuildRotatableTimeSec"] ?? 600f;

            // Ignore server give message
            Config["IgnoreGiveMessageEnabled"] = Config["IgnoreGiveMessageEnabled"] ?? true;
            Config["IgnoreGiveMessageAdmin"] = Config["IgnoreGiveMessageAdmin"] ?? true;
            Config["IgnoreGiveMessagePlayers"] = Config["IgnoreGiveMessagePlayers"] ?? new List<Object>();

            // Skins
            Config["SkinEnabled"] = Config["SkinEnabled"] ?? true;

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

                ["ObjectNotOwned"] = "That object isn't owned to you.",

                ["SkinInvalidCommand"] = "Invalid command. Type '/skin [SkinId]'.",
                ["SkinNotFound"] = "Skin not found.",
                ["SkinInvalidSkin"] = "This skin can't apply on current active item.",
                ["SkinActiveItemNotFound"] = "Active item not found. Hold item you want to apply.",
                ["SkinSuccess"] = "Applied to item successfully.",
                ["UnskinActiveItemNotFound"] = "Active item not found. Hold item you want to remove.",
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

                ["ObjectNotOwned"] = "해당 개체는 당신 소유가 아닙니다.",

                ["SkinInvalidCommand"] = "잘못된 입력입니다. '/skin [SkinId]' 형태로 입력해주세요.",
                ["SkinNotFound"] = "스킨을 찾을 수 없습니다.",
                ["SkinInvalidSkin"] = "이 스킨은 현재 들고 있는 아이템에 적용할 수 없습니다.",
                ["SkinActiveItemNotFound"] = "들고 있는 아이템이 없습니다. 적용하고 싶은 아이템을 들고 다시 시도해주세요.",
                ["SkinSuccess"] = "아이템에 성공적으로 적용되었습니다.",
                ["UnskinActiveItemNotFound"] = "들고 있는 아이템이 없습니다. 되돌리고 싶은 아이템을 들고 다시 시도해주세요.",
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
