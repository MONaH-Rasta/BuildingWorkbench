using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Building Workbench", "MJSU", "1.4.2")]
[Description("Extends the range of the workbench to work inside the entire building")]
public class BuildingWorkbench : RustPlugin
{
    #region Class Fields
    [PluginReference] private readonly Plugin GameTipAPI;

    private PluginConfig _pluginConfig;

    private WorkbenchBehavior _wb;
    private GameObject _go;
    private BuildingWorkbenchTrigger _tb;

    private const string UsePermission = "buildingworkbench.use";
    private const string CancelCraftPermission = "buildingworkbench.cancelcraft";
    private const string AccentColor = "#de8732";

    private readonly List<ulong> _notifiedPlayer = new();
    private readonly Dictionary<ulong, PlayerData> _playerData = new();
    private readonly Dictionary<uint, BuildingData> _buildingData = new();
    private float _scanRange;
    private float _halfScanRange;

    private PhysicsScene _physics;

    //private static BuildingWorkbench _ins;
    #endregion

    #region Setup & Loading
    private void Init()
    {
        //_ins = this;
        permission.RegisterPermission(UsePermission, this);
        permission.RegisterPermission(CancelCraftPermission, this);

        Unsubscribe(nameof(OnEntitySpawned));
        Unsubscribe(nameof(OnEntityKill));

        _scanRange = _pluginConfig.BaseDistance;
        _halfScanRange = _scanRange / 2f;
    }

    protected override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            [LangKeys.Chat] = $"<color=#bebebe>[<color={AccentColor}>{Title}</color>] {{0}}</color>",
            [LangKeys.Notification] = "Your workbench range has been increased to work inside your building",
            [LangKeys.CraftCanceled] = "Your workbench level has changed. Crafts that required a higher level have been cancelled."
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
        _physics = Physics.defaultPhysicsScene;
        if (_pluginConfig.BaseDistance < 3f)
        {
            PrintWarning("Distance from base to be considered inside building (Meters) cannot be less than 3 meters");
            _pluginConfig.BaseDistance = 3f;
        }

        _go = new GameObject("BuildingWorkbenchObject");
        _wb = _go.AddComponent<WorkbenchBehavior>();
        _tb = _go.AddComponent<BuildingWorkbenchTrigger>();

        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            OnPlayerConnected(player);
        }

        _wb.InvokeRepeating(StartUpdatingWorkbench, 1f, _pluginConfig.UpdateRate);

        Subscribe(nameof(OnEntitySpawned));
        Subscribe(nameof(OnEntityKill));
    }

    private void OnPlayerConnected(BasePlayer player)
    {
        player.nextCheckTime = float.MaxValue;
        player.EnterTrigger(_tb);
    }

    private void OnPlayerDisconnected(BasePlayer player)
    {
        player.nextCheckTime = 0;
        player.cachedCraftLevel = 0;
        if(_playerData.Remove(player.userID, out PlayerData playerData))
        {
            Dictionary<uint, BuildingData> buildingData = playerData.Buildings;
            foreach (BuildingData data in buildingData.Values)
            {
                data.LeaveBuilding(player);
            }
        }

        player.LeaveTrigger(_tb);
    }

    private void Unload()
    {
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            OnPlayerDisconnected(player);
        }

        if (_wb)
        {
            _wb.CancelInvoke(StartUpdatingWorkbench);
            _wb.StopAllCoroutines();
        }

        GameObject.Destroy(_go);
        //_ins = null;
    }
    #endregion

    #region Workbench Handler
    public void StartUpdatingWorkbench()
    {
        if (BasePlayer.activePlayerList.Count != 0)
        {
            _wb.StartCoroutine(HandleWorkbenchUpdate());
        }
    }

    public IEnumerator HandleWorkbenchUpdate()
    {
        float frameWait = 0;
        for (int i = 0; i < BasePlayer.activePlayerList.Count; i++)
        {
            BasePlayer player = BasePlayer.activePlayerList[i];

            if (!HasPermission(player, UsePermission))
            {
                if (player.nextCheckTime == float.MaxValue)
                {
                    player.nextCheckTime = 0;
                    player.cachedCraftLevel = 0;
                }

                continue;
            }

            PlayerData data = GetPlayerData(player.userID);
            if (Vector3.Distance(player.transform.position, data.Position) < _pluginConfig.RequiredDistance)
            {
                continue;
            }

            if (player.triggers == null)
            {
                player.EnterTrigger(_tb);
            }

            data.Position = player.transform.position;

            UpdatePlayerBuildings(player, data);
            UpdatePlayerWorkbenchLevel(player);

            float waitForFrames = Performance.report.frameRate * _pluginConfig.UpdateRate / BasePlayer.activePlayerList.Count * 0.9f;
            if (waitForFrames >= 1)
            {
                yield return null;
                continue;
            }

            frameWait += waitForFrames;
            if (frameWait >= 1)
            {
                frameWait -= 1f;
                yield return null;
            }
        }
    }

    public void UpdatePlayerBuildings(BasePlayer player, PlayerData data)
    {
        List<uint> currentBuildings = Pool.Get<List<uint>>();

        if (_pluginConfig.FastBuildingCheck)
        {
            GetNearbyAuthorizedBuildingsFast(player, currentBuildings);
        }
        else
        {
            GetNearbyAuthorizedBuildings(player, currentBuildings);
        }

        List<uint> leftBuildings = Pool.Get<List<uint>>();
        foreach (uint buildingId in data.Buildings.Keys)
        {
            if (!currentBuildings.Contains(buildingId))
            {
                leftBuildings.Add(buildingId);
            }
        }

        for (int index = 0; index < leftBuildings.Count; index++)
        {
            uint leftBuilding = leftBuildings[index];
            OnPlayerLeftBuilding(player, leftBuilding);
        }

        for (int index = 0; index < currentBuildings.Count; index++)
        {
            uint currentBuilding = currentBuildings[index];
            if (!data.Buildings.ContainsKey(currentBuilding))
            {
                OnPlayerEnterBuilding(player, currentBuilding);
            }
        }

        //Puts($"{nameof(BuildingData)}.{nameof(UpdatePlayerPriv)} {player.displayName} In: {string.Join(",", currentBuildings.Select(b => b.ToString().ToArray()))} Left: {string.Join(",", leftBuildings.Select(b => b.ToString().ToArray()))}");

        Pool.FreeUnmanaged(ref currentBuildings);
        Pool.FreeUnmanaged(ref leftBuildings);
    }

    public void OnPlayerEnterBuilding(BasePlayer player, uint buildingId)
    {
        BuildingData building = GetBuildingData(buildingId);
        building.EnterBuilding(player);
        Dictionary<uint, BuildingData> playerBuildings = GetPlayerData(player.userID).Buildings;
        playerBuildings[buildingId] = building;
    }

    public void OnPlayerLeftBuilding(BasePlayer player, uint buildingId)
    {
        BuildingData building = GetBuildingData(buildingId);
        building.LeaveBuilding(player);
        Dictionary<uint, BuildingData> playerBuildings = GetPlayerData(player.userID).Buildings;
        if (!playerBuildings.Remove(buildingId))
        {
            return;
        }

        if (player.inventory.crafting.queue.Count != 0 && HasPermission(player, CancelCraftPermission))
        {
            bool canceled = false;
            foreach (ItemCraftTask task in player.inventory.crafting.queue)
            {
                if (player.cachedCraftLevel < task.blueprint.workbenchLevelRequired)
                {
                    player.inventory.crafting.CancelTask(task.taskUID);
                    canceled = true;
                }
            }

            if (canceled && _pluginConfig.CancelCraftNotification)
            {
                Chat(player, Lang(LangKeys.CraftCanceled, player));
            }
        }
    }
    #endregion

    #region Oxide Hooks
    private void OnEntitySpawned(Workbench bench)
    {
        //Needs to be in NextTick since other plugins can spawn Workbenches
        NextTick(() =>
        {
            BuildingData data = GetBuildingData(bench.buildingID);
            data.OnBenchBuilt(bench);
            UpdateBuildingPlayers(data);

            if (!_pluginConfig.BuiltNotification)
            {
                return;
            }

            BasePlayer player = BasePlayer.FindByID(bench.OwnerID);
            if (!player)
            {
                return;
            }

            if (!HasPermission(player, UsePermission))
            {
                return;
            }

            if (_notifiedPlayer.Contains(player.userID.Get()))
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
        });
    }

    private void OnEntityKill(Workbench bench)
    {
        BuildingData data = GetBuildingData(bench.buildingID);
        data.OnBenchKilled(bench);
        UpdateBuildingPlayers(data);
    }

    private void OnEntityKill(BuildingPrivlidge tc)
    {
        OnCupboardClearList(tc);
    }

    private void OnEntityKill(PlayerBoatPrivilege privilege)
    {
        OnCupboardClearList(privilege);
    }

    private void OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player)
    {
        OnPlayerEnterBuilding(player, privilege.buildingID);
        UpdatePlayerWorkbenchLevel(player);
    }

    private void OnCupboardAuthorize(PlayerBoatPrivilege privilege, BasePlayer player)
    {
        if (privilege.ParentVehicle is PlayerBoat boat)
        {
            BuildingData data = GetBuildingData(boat);
            OnPlayerEnterBuilding(player, data.BuildingId);
            UpdatePlayerWorkbenchLevel(player);
        }
    }

    private void OnCupboardDeauthorize(BuildingPrivlidge privilege, BasePlayer player)
    {
        OnPlayerLeftBuilding(player, privilege.buildingID);
        UpdatePlayerWorkbenchLevel(player);
    }

    private void OnCupboardDeauthorize(PlayerBoatPrivilege privilege, BasePlayer player)
    {
        if (privilege.ParentVehicle is PlayerBoat boat)
        {
            BuildingData data = GetBuildingData(boat);
            OnPlayerLeftBuilding(player, data.BuildingId);
            UpdatePlayerWorkbenchLevel(player);
        }
    }

    private void OnCupboardClearList(BuildingPrivlidge privilege)
    {
        BuildingData data = GetBuildingData(privilege.buildingID);
        for (int index = data.Players.Count - 1; index >= 0; index--)
        {
            BasePlayer player = data.Players[index];
            OnPlayerLeftBuilding(player, privilege.buildingID);
            UpdatePlayerWorkbenchLevel(player);
        }
    }

    private void OnCupboardClearList(PlayerBoatPrivilege privilege)
    {
        if (privilege.ParentVehicle is not PlayerBoat boat)
        {
            return;
        }
        BuildingData data = GetBuildingData(boat);
        for (int index = data.Players.Count - 1; index >= 0; index--)
        {
            BasePlayer player = data.Players[index];
            OnPlayerLeftBuilding(player, data.BuildingId);
            UpdatePlayerWorkbenchLevel(player);
        }
    }

    private void OnEntityEnter(TriggerWorkbench trigger, BasePlayer player)
    {
        if (!player.IsNpc)
        {
            UpdatePlayerWorkbenchLevel(player);
        }
    }

    private void OnEntityLeave(TriggerWorkbench trigger, BasePlayer player)
    {
        if (!player.IsNpc)
        {
            NextTick(() =>
            {
                UpdatePlayerWorkbenchLevel(player);
            });
        }
    }

    private void OnEntityLeave(BuildingWorkbenchTrigger trigger, BasePlayer player)
    {
        if (player.IsNpc)
        {
            return;
        }

        //_ins.Puts($"{nameof(BuildingWorkbench)}.{nameof(OnEntityLeave)} {nameof(BuildingWorkbenchTrigger)} {player.displayName}");

        NextTick(() =>
        {
            player.EnterTrigger(_tb);
        });
    }
    #endregion

    #region Helper Methods
    public void UpdateBuildingPlayers(BuildingData building)
    {
        for (int index = 0; index < building.Players.Count; index++)
        {
            BasePlayer player = building.Players[index];
            UpdatePlayerWorkbenchLevel(player);
        }
    }

    public void UpdatePlayerWorkbenchLevel(BasePlayer player)
    {
        byte level = 0;

        PlayerData playerData = GetPlayerData(player.userID);
        Dictionary<uint, BuildingData> playerBuildings = playerData.Buildings;
        if (playerBuildings != null)
        {
            foreach (BuildingData building in playerBuildings.Values)
            {
                level = Math.Max(level, building.GetWorkbenchLevel());
            }
        }

        if (level != 3 && player.triggers != null)
        {
            for (int index = 0; index < player.triggers.Count; index++)
            {
                TriggerWorkbench trigger = player.triggers[index] as TriggerWorkbench;
                if (trigger)
                {
                    level = Math.Max(level, (byte)trigger.parentBench.Workbenchlevel);
                }
            }
        }

        if ((byte)player.cachedCraftLevel == level && playerData.WorkbenchLevel == level)
        {
            return;
        }

        //_ins.Puts($"{nameof(BuildingWorkbench)}.{nameof(UpdatePlayerWorkbenchLevel)} {player.displayName} -> {level}");
        player.nextCheckTime = float.MaxValue;
        player.cachedCraftLevel = level;
        playerData.WorkbenchLevel = level;
        player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench1, level == 1);
        player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench2, level == 2);
        player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench3, level == 3);
        player.SendNetworkUpdateImmediate();
    }

    public bool TryGetPlayerBoat(BaseEntity entity, out PlayerBoat boat)
    {
        boat = PlayerBoat.GetParentPlayerBoat(entity);
        return boat;
    }

    public bool TryGetPlayerBoatBuildingId(PlayerBoat boat, out uint buildingId)
    {
        if (boat && boat.BoatBuildingBlocks.Cached.Count != 0)
        {
            buildingId = boat.BoatBuildingBlocks.Cached[0].buildingID;
            return true;
        }

        buildingId = 0;
        return false;
    }

    public PlayerData GetPlayerData(ulong playerId)
    {
        if (!_playerData.TryGetValue(playerId, out PlayerData data))
        {
            _playerData[playerId] = data = new PlayerData();
        }

        return data;
    }

    public BuildingData GetBuildingData(uint buildingId)
    {
        if (!_buildingData.TryGetValue(buildingId, out BuildingData data))
        {
            _buildingData[buildingId] = data = new BuildingData(buildingId);
        }

        return data;
    }

    public BuildingData GetBuildingData(PlayerBoat boat)
    {
        return TryGetPlayerBoatBuildingId(boat, out uint buildingId) ? GetBuildingData(buildingId) : null;
    }

    private readonly RaycastHit[] _hits = new RaycastHit[256];
    private readonly List<uint> _processedBuildings = new();

    public void GetNearbyAuthorizedBuildingsFast(BasePlayer player, List<uint> authorizedPrivs)
    {
        OBB obb = player.WorldSpaceBounds();
        float baseDistance = _scanRange;
        int amount = _physics.Raycast(player.transform.position + Vector3.down * _halfScanRange, Vector3.up, _hits, baseDistance, Rust.Layers.Construction, QueryTriggerInteraction.Ignore);
        for (int index = 0; index < amount; index++)
        {
            BuildingBlock block = _hits[index].transform.ToBaseEntity() as BuildingBlock;
            if (!block)
            {
                continue;
            }

            if (_processedBuildings.Contains(block.buildingID) || obb.Distance(block.WorldSpaceBounds()) > baseDistance)
            {
                continue;
            }

            _processedBuildings.Add(block.buildingID);
            BuildingPrivlidge priv = block.GetBuilding()?.GetDominatingBuildingPrivilege();
            if (!priv || !priv.IsAuthed(player))
            {
                continue;
            }

            authorizedPrivs.Add(priv.buildingID);
        }
        _processedBuildings.Clear();
    }

    public void GetNearbyAuthorizedBuildings(BasePlayer player, List<uint> authorizedPrivs)
    {
        OBB obb = player.WorldSpaceBounds();
        float baseDistance = _pluginConfig.BaseDistance;
        int amount = _physics.OverlapSphere(obb.position, baseDistance + obb.extents.magnitude, Vis.colBuffer, Rust.Layers.Construction | Rust.Layers.VehiclesLarge, QueryTriggerInteraction.Ignore);
        for (int index = 0; index < amount; index++)
        {
            Collider collider = Vis.colBuffer[index];
            BuildingBlock block = collider.ToBaseEntity() as BuildingBlock;
            if (!block)
            {
                continue;
            }

            if (_processedBuildings.Contains(block.buildingID) || obb.Distance(block.WorldSpaceBounds()) > baseDistance)
            {
                continue;
            }

            _processedBuildings.Add(block.buildingID);
            if (block is BoatBuildingBlock boatBlock)
            {
                if(TryGetPlayerBoat(boatBlock, out PlayerBoat boat) && boat.IsAuthedForBuilding(player))
                {
                    authorizedPrivs.Add(boatBlock.buildingID);
                }
            }
            else
            {
                BuildingPrivlidge priv = block.GetBuilding()?.GetDominatingBuildingPrivilege();
                if (priv && priv.IsAuthed(player))
                {
                    authorizedPrivs.Add(priv.buildingID);
                }
            }
        }

        _processedBuildings.Clear();
    }

    public void Chat(BasePlayer player, string message) => PrintToChat(player, Lang(LangKeys.Chat, player, message));

    public bool HasPermission(BasePlayer player, string perm) => permission.UserHasPermission(player.UserIDString, perm);

    private string Lang(string key, BasePlayer player = null)
    {
        return lang.GetMessage(key, this, player?.UserIDString);
    }

    private string Lang(string key, BasePlayer player = null, params object[] args)
    {
        try
        {
            return string.Format(Lang(key, player), args);
        }
        catch (Exception ex)
        {
            PrintError($"Lang Key '{key}' threw exception\n:{ex}");
            throw;
        }
    }
    #endregion

    #region Building Data
    public class BuildingData
    {
        public readonly uint BuildingId;
        public Workbench BestWorkbench { get; protected set; }
        public readonly List<BasePlayer> Players = new();
        public List<Workbench> Workbenches { get; protected set; }

        public BuildingData(uint buildingId)
        {
            BuildingId = buildingId;
            Workbenches = BuildingManager.server.GetBuilding(buildingId)?.decayEntities.OfType<Workbench>().ToList() ?? new List<Workbench>();
            UpdateBestBench();
        }

        public void EnterBuilding(BasePlayer player)
        {
            //_ins.Puts($"{nameof(BuildingData)}.{nameof(EnterBuilding)} {player.displayName} {GetWorkbenchLevel()}");
            Players.Add(player);
        }

        public void LeaveBuilding(BasePlayer player)
        {
            //_ins.Puts($"{nameof(BuildingData)}.{nameof(LeaveBuilding)} {player.displayName} {GetWorkbenchLevel()}");
            Players.Remove(player);
        }

        public void OnBenchBuilt(Workbench workbench)
        {
            Workbenches.Add(workbench);
            UpdateBestBench();
        }

        public void OnBenchKilled(Workbench workbench)
        {
            Workbenches.Remove(workbench);
            UpdateBestBench();
        }

        public byte GetWorkbenchLevel()
        {
            if (!BestWorkbench)
            {
                return 0;
            }

            return (byte)BestWorkbench.Workbenchlevel;
        }

        private void UpdateBestBench()
        {
            BestWorkbench = null;
            for (int index = 0; index < Workbenches.Count; index++)
            {
                Workbench workbench = Workbenches[index];
                if (!BestWorkbench || BestWorkbench.Workbenchlevel < workbench.Workbenchlevel)
                {
                    BestWorkbench = workbench;
                }
            }
        }
    }
    #endregion

    #region Classes
    private class PluginConfig
    {
        [DefaultValue(true)]
        [JsonProperty(PropertyName = "Display workbench built notification")]
        public bool BuiltNotification { get; set; }

        [DefaultValue(true)]
        [JsonProperty(PropertyName = "Display cancel craft notification")]
        public bool CancelCraftNotification { get; set; }

        [DefaultValue(3f)]
        [JsonProperty(PropertyName = "Inside building check frequency (Seconds)")]
        public float UpdateRate { get; set; }

        [DefaultValue(false)]
        [JsonProperty(PropertyName = "Enable Fast Building Check (Only checks above and below a player)")]
        public bool FastBuildingCheck { get; set; }

        [DefaultValue(true)]
        [JsonProperty(PropertyName = "Enable Boat Check")]
        public bool EnableBoatCheck { get; set; } = true;

        [DefaultValue(16f)]
        [JsonProperty(PropertyName = "Distance from base to be considered inside building (Meters)")]
        public float BaseDistance { get; set; }

        [DefaultValue(5)]
        [JsonProperty(PropertyName = "Required distance from last update (Meters)")]
        public float RequiredDistance { get; set; }
    }

    public class PlayerData
    {
        public Vector3 Position { get; set; }
        public Dictionary<uint, BuildingData> Buildings { get; } = new();
        public byte WorkbenchLevel { get; set; }
    }

    private class LangKeys
    {
        public const string Chat = nameof(Chat);
        public const string Notification = nameof(Notification);
        public const string CraftCanceled = nameof(CraftCanceled) + "V1";
    }

    public class WorkbenchBehavior : FacepunchBehaviour
    {

    }

    public class BuildingWorkbenchTrigger : TriggerBase
    {

    }
    #endregion
}