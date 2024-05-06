using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Building Workbench", "MJSU", "1.0.4")]
    [Description("Extends the range of the workbench to work inside the entire building")]
    public class BuildingWorkbench : RustPlugin
    {
        #region Class Fields
        [PluginReference] private readonly Plugin GameTipAPI;

        private PluginConfig _pluginConfig; //Plugin Config

        private TriggerBase _triggerBase;
        private GameObject _object;

        private const string UsePermission = "buildingworkbench.use";
        private const string AccentColor = "#de8732";

        private readonly List<ulong> _notifiedPlayer = new List<ulong>();
        private readonly Hash<uint, BuildingData> _buildingData = new Hash<uint, BuildingData>();
        private readonly Hash<ulong, uint> _playerBuilding = new Hash<ulong, uint>();

        private Coroutine _routine;
        
        private static BuildingWorkbench _ins;

        private bool _init;
        #endregion

        #region Setup & Loading
        private void Init()
        {
            _ins = this;
            permission.RegisterPermission(UsePermission, this);
        }
        
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [LangKeys.Chat] = $"<color=#bebebe>[<color={AccentColor}>{Title}</color>] {{0}}</color>",
                [LangKeys.Notification] = "Your workbench range has been increased to work inside your building"
            }, this);
        }
        
        protected override void LoadDefaultConfig()
        {
            PrintWarning("Loading Default Config");
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            Config.Settings.DefaultValueHandling = DefaultValueHandling.Populate;
            _pluginConfig = Config.ReadObject<PluginConfig>();
            Config.WriteObject(_pluginConfig);
        }

        private void OnServerInitialized()
        {
             _object = new GameObject("BuildingWorkbenchObject");
             _triggerBase = _object.AddComponent<TriggerBase>();
            
            foreach (Workbench workbench in GameObject.FindObjectsOfType<Workbench>())
            {
                BuildingData data = GetBuildingData(workbench.buildingID);
                data.AddWorkbench(workbench);
            }
            
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                OnPlayerInit(player);
            }
            
            InvokeHandler.Instance.InvokeRepeating(StartUpdatingWorkbench, 1f, _pluginConfig.UpdateRate);
            
            _init = true;
        }

        private void OnPlayerInit(BasePlayer player)
        {
            if (player.triggers == null || !player.triggers.Contains(_triggerBase))
            {
                player.EnterTrigger(_triggerBase);
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            player.LeaveTrigger(_triggerBase);
        }

        private void Unload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                player.LeaveTrigger(_triggerBase);
            }
            
            InvokeHandler.Instance.CancelInvoke(StartUpdatingWorkbench);
            if (_routine != null)
            {
                InvokeHandler.Instance.StopCoroutine(_routine);
            }
            
            GameObject.Destroy(_object);
            _ins = null;
        }
        #endregion

        #region Workbench Handler

        private void StartUpdatingWorkbench()
        {
            if (BasePlayer.activePlayerList.Count == 0)
            {
                return;
            }
            
            _routine = InvokeHandler.Instance.StartCoroutine(HandleWorkbenchUpdate());
        }

        private IEnumerator HandleWorkbenchUpdate()
        {
            for (int i = 0; i < BasePlayer.activePlayerList.Count; i++)
            {
                BasePlayer player = BasePlayer.activePlayerList[0];
                yield return null;

                if (!HasPermission(player, UsePermission))
                {
                    continue;
                }
                
                UpdatePlayerPriv(player);
            }
        }

        private void UpdatePlayerPriv(BasePlayer player)
        {
            BuildingPrivlidge priv = player.GetBuildingPrivilege();
            uint prevBuilding = _playerBuilding[player.userID];
            if (priv == null || prevBuilding == 0 || priv.buildingID != prevBuilding)
            {
                BuildingData prevData = GetBuildingData(prevBuilding);
                prevData.RemovePlayer(player);
            }
            
            if (priv == null || !priv.IsAuthed(player))
            {
                UpdatePlayerBench(player, 0);
                return;
            }
            
            BuildingData data = GetBuildingData(priv.buildingID);
            if (prevBuilding != priv.buildingID)
            {
                _playerBuilding[player.userID] = priv.buildingID;
                data.AddPlayer(player);
            }
            
            UpdatePlayerBench(player, data.WorkbenchLevel);
        }

        private void UpdatePlayerBench(BasePlayer player, int level)
        {
            player.nextCheckTime = Time.realtimeSinceStartup + _ins._pluginConfig.UpdateRate + 1f;
            player.cachedCraftLevel = level;
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench1, level == 1);
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench2, level == 2);
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench3, level == 3);
            player.SendNetworkUpdateImmediate();
        }

        private void UpdateBuildingPlayers(BuildingData data, BasePlayer player = null)
        {
            int level = data.WorkbenchLevel;
            foreach (BasePlayer buildingPlayer in data.BuildingPlayers)
            {
                UpdatePlayerBench(buildingPlayer, level);
            }

            if (player != null)
            {
                UpdatePlayerBench(player, level);
            }
        }
        #endregion

        #region Oxide Hooks
        private void OnEntityLeave(TriggerBase trigger, BaseEntity entity)
        {
            BasePlayer player = entity.ToPlayer();
            if (player != null && trigger == _triggerBase)
            {
                player.EnterTrigger(_triggerBase);
            }
        }

        private void OnEntitySpawned(Workbench bench)
        {
            if (!_init)
            {
                return;
            }
            
            BasePlayer player = BasePlayer.FindByID(bench.OwnerID);
            if (player == null)
            {
                return;
            }

            BuildingData data = GetBuildingData(bench.buildingID);
            data.AddWorkbench(bench);

            UpdateBuildingPlayers(data, player);

            if (!_pluginConfig.EnableNotifications)
            {
                return;
            }
            
            if (_notifiedPlayer.Contains(player.userID))
            {
                return;
            }
            
            _notifiedPlayer.Add(player.userID);
            
            if (GameTipAPI == null)
            {
                Chat(player, Lang(LangKeys.Notification, player));
            }
            else
            {
                GameTipAPI.Call("ShowGameTip", player, Lang(LangKeys.Notification, player), 6f);
            }
        }

        private void OnEntityKill(Workbench bench)
        {
            BuildingData data = GetBuildingData(bench.buildingID);
            data.RemoveWorkbench(bench);
            UpdateBuildingPlayers(data);
        }
        
        private void OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player)
        {
            OnAuthChanged(player);
        }
        
        private void OnCupboardDeauthorize(BuildingPrivlidge privilege, BasePlayer player)
        {
            OnAuthChanged(player);
        }
        
        private void OnAuthChanged(BasePlayer player)
        {
            NextTick(() =>
            {
                UpdatePlayerPriv(player);
            });
        }

        private void OnCupboardClearList(BuildingPrivlidge privilege, BasePlayer player)
        {
            NextTick(() =>
            {
                UpdatePlayerPriv(player);
                List<BasePlayer> players = GetBuildingData(privilege.buildingID).BuildingPlayers;
                for (int i = players.Count - 1; i >= 0; i--)
                {
                    UpdatePlayerPriv(players[i]);
                }
            });
        }
        #endregion

        #region Helper Methods
        private BuildingData GetBuildingData(uint buildingId)
        {
            BuildingData data = _buildingData[buildingId];
            if (data == null)
            {
                data = new BuildingData();
                _buildingData[buildingId] = data;
            }

            return data;
        }
        
        private void Chat(BasePlayer player, string format, params object[] args) => PrintToChat(player, Lang(LangKeys.Chat, player, format), args);
        
        private bool HasPermission(BasePlayer player, string perm) => permission.UserHasPermission(player.UserIDString, perm);
        
        private string Lang(string key, BasePlayer player = null, params object[] args) => string.Format(lang.GetMessage(key, this, player?.UserIDString), args);
        #endregion

        #region Classes
        private class PluginConfig
        {
            [DefaultValue(true)]
            [JsonProperty(PropertyName = "Enable Notifications")]
            public bool EnableNotifications { get; set; }
            
            [DefaultValue(3f)]
            [JsonProperty(PropertyName = "Update Rate (Seconds)")]
            public float UpdateRate { get; set; }
        }
        
        private class LangKeys
        {
            public const string Chat = "Chat";
            public const string Notification = "Notification";
        }

        private class BuildingData
        {
            public List<Workbench> Workbenches = new List<Workbench>();
            public readonly List<BasePlayer> BuildingPlayers = new List<BasePlayer>();

            public int WorkbenchLevel => Workbenches.Count == 0 ? 0 : Workbenches.Max(w => w.Workbenchlevel);

            public void AddPlayer(BasePlayer player)
            {
                if (!BuildingPlayers.Contains(player))
                {
                    BuildingPlayers.Add(player);
                }
            }

            public void RemovePlayer(BasePlayer player)
            {
                BuildingPlayers.Remove(player);
            }

            public void AddWorkbench(Workbench bench)
            {
                if (!Workbenches.Contains(bench))
                {
                    Workbenches.Add(bench);
                }
            }

            public void RemoveWorkbench(Workbench bench)
            {
                Workbenches.Remove(bench);
            }
        }
        #endregion
    }
}
