using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Building Workbench", "MJSU", "1.0.3")]
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

        private static BuildingWorkbench _ins;

        private bool _init;
        #endregion

        #region Setup & Loading
        private void Init()
        {
            _ins = this;
            permission.RegisterPermission(UsePermission, this);

            _object = new GameObject("BuildingWorkbenchObject");
            _triggerBase = _object.AddComponent<TriggerBase>();
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
            foreach (Workbench workbench in GameObject.FindObjectsOfType<Workbench>())
            {
                AddWorkbench(workbench);
            }
            
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                OnPlayerInit(player);
            }
            
            _init = true;
        }

        private void OnPlayerInit(BasePlayer player)
        {
            if (!HasPermission(player, UsePermission))
            {
                return;
            }

            if (player.GetComponent<WorkbenchBehavior>() != null)
            {
                return;
            }

            if (!player.triggers?.Contains(_triggerBase) ?? false)
            {
                player.EnterTrigger(_triggerBase);
            }

            player.gameObject.AddComponent<WorkbenchBehavior>();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            player.gameObject.GetComponent<WorkbenchBehavior>()?.DoDestroy();
            player.LeaveTrigger(_triggerBase);
        }

        private void Unload()
        {
            foreach (WorkbenchBehavior behavior in GameObject.FindObjectsOfType<WorkbenchBehavior>())
            {
                behavior.DoDestroy();
            }
            GameObject.Destroy(_object);
            _ins = null;
        }
        #endregion

        #region Permission Hooks
        private void OnUserPermissionGranted(string playerId, string permName)
        {
            if (permName != UsePermission)
            {
                return;
            }
            
            HandleUserChanges(playerId);
        }
        
        private void OnUserPermissionRevoked(string playerId, string permName)
        {
            if (permName != UsePermission)
            {
                return;
            }
            
            HandleUserChanges(playerId);
        }
        
        private void OnUserGroupAdded(string playerId, string groupName)
        {
            HandleUserChanges(playerId);
        }
        
        private void OnUserGroupRemoved(string playerId, string groupName)
        {
            HandleUserChanges(playerId);
        }

        private void OnGroupPermissionGranted(string groupName, string permName)
        {
            if (permName != UsePermission)
            {
                return;
            }

            NextTick(() =>
            {
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    HandleUserChanges(player);
                }
            });
        }
        
        private void OnGroupPermissionRevoked(string groupName, string permName)
        {
            if (permName != UsePermission)
            {
                return;
            }
            
            NextTick(() =>
            {
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    HandleUserChanges(player);
                }
            });
        }

        private void HandleUserChanges(string id)
        {
            NextTick(() =>
            {
                BasePlayer player = BasePlayer.Find(id);
                if (player == null)
                {
                    return;
                }

                HandleUserChanges(player);
            });
        }

        private void HandleUserChanges(BasePlayer player)
        {
            bool hasPerm = HasPermission(player, UsePermission);
            bool hasBehavior = player.GetComponent<WorkbenchBehavior>() != null;
            if (hasPerm == hasBehavior)
            {
                return;
            }

            if (hasBehavior)
            {
                OnPlayerDisconnected(player, string.Empty);
            }
            else
            {
                OnPlayerInit(player);
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

            AddWorkbench(bench);
            
            List<WorkbenchBehavior> behaviors = GetBuildingData(bench.buildingID).Behaviors;
            for (int i = behaviors.Count - 1; i >= 0; i--)
            {
                behaviors[i].OnWorkbenchChanged();
            }

            WorkbenchBehavior benchBehavior = player.GetComponent<WorkbenchBehavior>();
            if (benchBehavior == null)
            {
                return;
            }
            
            benchBehavior.OnWorkbenchChanged();

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
            RemoveWorkbench(bench);
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
                player.GetComponent<WorkbenchBehavior>()?.OnAuthChanged();
            });
        }

        private void OnCupboardClearList(BuildingPrivlidge privilege, BasePlayer player)
        {
            NextTick(() =>
            {
                player.GetComponent<WorkbenchBehavior>()?.OnAuthChanged();

                List<WorkbenchBehavior> behaviors = GetBuildingData(privilege.buildingID).Behaviors;
                for (int i = behaviors.Count - 1; i >= 0; i--)
                {
                    behaviors[i].OnAuthChanged();
                }
            });
        }
        #endregion

        #region Behavior
        private class WorkbenchBehavior : FacepunchBehaviour
        {
            private BasePlayer Player { get; set; }
            private bool IsInsideBuildingPrivilege { get; set; }
            private uint BuildingId { get; set; }
            private bool ForceUpdate { get; set; }

            private void Awake()
            {
                enabled = false;
                Player = GetComponent<BasePlayer>();
                InvokeRepeating(CheckInsideBuildingPrivilege, 1f, _ins._pluginConfig.InsideBuildingPrivilegeInterval);
            }

            public void OnAuthChanged()
            {
                CheckInsideBuildingPrivilege();
                ForceUpdatePlayer();
            }
            
            public void OnWorkbenchChanged()
            {
                ForceUpdatePlayer();
            }

            private void ForceUpdatePlayer()
            {
                ForceUpdate = true;
                UpdatePlayerWorkbenchLevel();
            }

            private void CheckInsideBuildingPrivilege()
            {
                BuildingPrivlidge priv = Player.GetBuildingPrivilege();
                bool isAuthed = priv?.IsAuthed(Player) ?? false;
                
                if (!IsInsideBuildingPrivilege && isAuthed)
                {
                    IsInsideBuildingPrivilege = true;
                    BuildingId = priv.buildingID;
                    _ins.AddBuilding(BuildingId, this);
                    InvokeRandomized(UpdatePlayerWorkbenchLevel, 0, _ins._pluginConfig.UpdateWorkbenchLevelInterval, 0.1f);
                    //_ins.Puts($" Activating {_workbenchLevel}");
                }
                else if (IsInsideBuildingPrivilege && !isAuthed)
                {
                    IsInsideBuildingPrivilege = false;
                    _ins.RemoveBuilding(BuildingId, this);
                    BuildingId = 0;
                    CancelInvoke(UpdatePlayerWorkbenchLevel);
                    ForceUpdatePlayer();
                    //_ins.Puts("Deactivating");
                }

                if (isAuthed && priv.buildingID != BuildingId)
                {
                    _ins.RemoveBuilding(BuildingId, this);
                    BuildingId = priv.buildingID;
                    _ins.AddBuilding(BuildingId, this);
                }
            }

            private int GetBuildingWorkbenchLevel()
            {
                if (BuildingId == 0)
                {
                    return 0;
                }

                BuildingData buildingBenches = _ins.GetBuildingData(BuildingId);
                return buildingBenches.Workbenches.Count == 0 ? 0 : buildingBenches.Workbenches.Max(b => b.Workbenchlevel);
            }

            private void UpdatePlayerWorkbenchLevel()
            {
                int workbenchLevel = GetBuildingWorkbenchLevel();
                if (!ForceUpdate && (workbenchLevel <= 0 || !IsInsideBuildingPrivilege))
                {
                    return;
                }

                //_ins.Puts("Updating Level");
                Player.nextCheckTime = Time.realtimeSinceStartup + _ins._pluginConfig.UpdateWorkbenchLevelInterval + .5f;

                Player.cachedCraftLevel = workbenchLevel;

                Player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench1, workbenchLevel == 1);
                Player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench2, workbenchLevel == 2);
                Player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench3, workbenchLevel == 3);
                ForceUpdate = false;
            }

            public void DoDestroy()
            {
                _ins._buildingData[BuildingId]?.Behaviors.Remove(this);
                Destroy(this);
            }
        }
        #endregion

        #region Helper Methods

        private void AddWorkbench(Workbench bench)
        {
            BuildingData data = GetBuildingData(bench.buildingID);
            data.Workbenches.Add(bench);
        }

        private void RemoveWorkbench(Workbench bench)
        {
            BuildingData data = GetBuildingData(bench.buildingID);
            data.Workbenches.Remove(bench);
            
            foreach (WorkbenchBehavior behavior in data.Behaviors)
            {
                behavior.OnWorkbenchChanged();
            }
        }

        private void AddBuilding(uint buildingId, WorkbenchBehavior behavior)
        {
            BuildingData data = GetBuildingData(buildingId);
            data.Workbenches = BuildingManager.server.GetBuilding(buildingId).decayEntities.OfType<Workbench>().ToList();
            data.Behaviors.Add(behavior);
        }
        
        private void RemoveBuilding(uint buildingId, WorkbenchBehavior behavior)
        {
            BuildingData data = GetBuildingData(buildingId);
            data.Behaviors.Remove(behavior);
        }

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
            [JsonProperty(PropertyName = "Inside Building Privilege Interval")]
            public float InsideBuildingPrivilegeInterval { get; set; }

            [DefaultValue(3f)]
            [JsonProperty(PropertyName = "Update Workbench Level Interval")]
            public float UpdateWorkbenchLevelInterval { get; set; }
        }
        
        private class LangKeys
        {
            public const string Chat = "Chat";
            public const string Notification = "Notification";
        }

        private class BuildingData
        {
            public List<Workbench> Workbenches = new List<Workbench>();
            public readonly List<WorkbenchBehavior> Behaviors = new List<WorkbenchBehavior>();
        }
        #endregion
    }
}
