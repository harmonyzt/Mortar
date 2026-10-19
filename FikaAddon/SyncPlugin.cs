using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using Comfort.Common;
using CommonAssets.Scripts.ArtilleryShelling;
using EFT;
using EFT.GlobalEvents;
using EFT.GlobalEvents.ArtilleryShellingEcents;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using HarmonyLib;
using Mortair;
using UnityEngine;

namespace Mortar.Fika
{
    [BepInPlugin("com.harmonyzt.Mortar.Fika", "Mortair Fika Sync", "1.0.0")]
    [BepInDependency("com.harmonyzt.Mortair", "1.1.0")]
    [BepInDependency("com.fika.core", "2.4.6")]
    public sealed class SyncPlugin : BaseUnityPlugin, IMortarNetwork
    {
        internal static SyncPlugin Instance;
        private const string ProtocolVersion = "2";
        private IFikaNetworkManager _manager;
        private Harmony _harmony;
        private Action _unsubscribe;
        private bool _ready;
        private bool _hostReady;
        private readonly Dictionary<string, int[]> _warnings = new Dictionary<string, int[]>();
        private readonly Dictionary<int, MortairPacket> _eyeStates = new Dictionary<int, MortairPacket>();
        private readonly Dictionary<int, AlertState> _alerts = new Dictionary<int, AlertState>();
        private readonly Dictionary<NetPeer, int> _waitingPeers = new Dictionary<NetPeer, int>();
        public bool IsClient => FikaBackendUtils.IsClient;

        private void Awake()
        {
            Instance = this;
            CoopSupport.Network = this;

            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
            FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(OnGameEnded);

            EyeWarning.HoldChanged += OnEyeChanged;
            _unsubscribe = GlobalEventsController.Instance.SubscribeOnEvent<ShellingNotifyEvent>(OnWarning);
            _unsubscribe +=
                GlobalEventsController.Instance.SubscribeOnEvent<ChangePlayerShellingAlertStateEvent>(OnAlert);
            _unsubscribe += GlobalEventsController.Instance.SubscribeOnEvent<NotifyEvent>(OnNotify);
            _unsubscribe +=
                GlobalEventsController.Instance.SubscribeOnEvent<LastShellingExplosionZoneEvent>(OnZoneEnded);

            _harmony = new Harmony("com.harmonyzt.Mortar.Fika");
            _harmony.PatchAll(typeof(SyncPlugin).Assembly);

            Logger.LogInfo("[Mortair] Fika sync loaded.");
        }

        private void OnDestroy()
        {
            FikaEventDispatcher.UnsubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
            FikaEventDispatcher.UnsubscribeEvent<FikaGameEndedEvent>(OnGameEnded);

            EyeWarning.HoldChanged -= OnEyeChanged;

            _unsubscribe?.Invoke();
            _harmony?.UnpatchSelf();

            if (CoopSupport.Network == this)
            {
                CoopSupport.Network = null;
                CoopSupport.HostRules = null;
            }

            Instance = null;
        }

        private void OnManagerCreated(FikaNetworkManagerCreatedEvent ev)
        {
            _manager = ev.Manager;
            _waitingPeers.Clear();
            BeginRaid();
            _manager.RegisterPacket<MortairPacket, NetPeer>(Receive);
        }

        private void OnGameEnded(FikaGameEndedEvent ev)
        {
            _waitingPeers.Clear();
            BeginRaid();
        }

        internal void BeginRaid()
        {
            _ready = false;
            _hostReady = false;
            _warnings.Clear();
            _alerts.Clear();
            _eyeStates.Clear();
            CoopSupport.HostRules = null;
        }

        internal void ClientReady()
        {
            if (IsClient)
            {
                Send(new MortairPacket
                {
                    Kind = PacketKind.Ready, Text = ProtocolVersion,
                    NetId = (Singleton<GameWorld>.Instance?.MainPlayer as FikaPlayer)?.NetId ?? -1
                });
            }
        }

        internal void HostReady()
        {
            if (!FikaBackendUtils.IsServer)
            {
                return;
            }

            _hostReady = true;

            foreach (var entry in _waitingPeers.ToArray())
            {
                SendSettings(entry.Key, entry.Value);
            }
        }

        public bool RequestStrike(Vector3 position, string reason, IPlayer source)
        {
            if (!_ready || _manager == null || !(source is FikaPlayer player) || !player.IsYourPlayer)
            {
                return false;
            }

            Send(new MortairPacket
                { Kind = PacketKind.Strike, NetId = player.NetId, Position = position, Text = reason });

            return true;
        }

        private static FikaPlayer Find(int netId)
        {
            return CoopHandler.TryGetCoopHandler(out CoopHandler handler) &&
                   handler.Players.TryGetValue(netId, out FikaPlayer player)
                ? player
                : null;
        }

        private static FikaPlayer FindPlayerId(int playerId)
        {
            return CoopHandler.TryGetCoopHandler(out CoopHandler handler)
                ? handler.HumanPlayers.FirstOrDefault(player => player.PlayerId == playerId)
                : null;
        }

        private void Receive(MortairPacket packet, NetPeer peer)
        {
            try
            {
                if (FikaBackendUtils.IsServer)
                {
                    if (packet.Kind == PacketKind.Ready)
                    {
                        if (packet.Text != ProtocolVersion)
                        {
                            throw new InvalidOperationException("Mortair sync protocol mismatch.");
                        }

                        SendSettings(peer, packet.NetId);

                        return;
                    }

                    FikaPlayer player = Find(packet.NetId);
                    if (!(player is ObservedPlayer) || player.IsAI)
                    {
                        return;
                    }

                    if (packet.Kind == PacketKind.Strike && CoopSupport.Enabled && player.HealthController.IsAlive)
                    {
                        if ((packet.Text == "hotkey" && CoopSupport.GetLocalRules().AllowHotkeys) ||
                            (packet.Text == "flare" && CoopSupport.GetLocalRules().FlareMode != EFlareTriggerMode.Off))
                        {
                            StrikeService.TryCallStrike(packet.Position, packet.Text, player);
                        }
                    }
                    else if (packet.Kind == PacketKind.Eye && (!packet.Flag || player.HealthController.IsAlive))
                    {
                        ApplyEye(packet);
                        Send(packet);
                    }

                    return;
                }

                if (!IsClient)
                {
                    return;
                }

                if (packet.Kind == PacketKind.Settings)
                {
                    ApplySettings(packet.Text);
                    return;
                }

                FikaPlayer target = Find(packet.NetId);
                Player local = Singleton<GameWorld>.Instance?.MainPlayer;
                if (local == null)
                {
                    return;
                }

                switch (packet.Kind)
                {
                    case PacketKind.Warning:
                        if (packet.Flag || target == local)
                        {
                            GlobalEventsController.CreateEvent<ShellingNotifyEvent>()
                                .Invoke(new[] { local.PlayerId }, packet.Text);
                        }

                        break;
                    case PacketKind.Alert:
                        _alerts[packet.NetId] = new AlertState { Zone = packet.Text, Point = packet.Position };
                        if (target != null)
                        {
                            GlobalEventsController.CreateEvent<ChangePlayerShellingAlertStateEvent>()
                                .Invoke(target.PlayerId, packet.Text, packet.Position);
                        }

                        break;
                    case PacketKind.Busy:
                        if (target == local)
                        {
                            NotifyEvent ev = GlobalEventsController.CreateEvent<NotifyEvent>();
                            ev.MessageType = NotifyEvent.EMessageType.ArtilleryBusy;
                            ev.ReceiverId = local.PlayerId;
                            ev.Invoke();
                        }

                        break;
                    case PacketKind.Eye:
                        ApplyEye(packet);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Mortair] sync packet {packet.Kind} failed: {ex}");
            }
        }

        private void Send(MortairPacket packet, NetPeer peer = null)
        {
            if (_manager == null)
            {
                return;
            }

            if (peer == null)
            {
                _manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                _manager.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
            }
        }

        private void SendSettings(NetPeer peer, int recipientId)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            var globals = Singleton<GlobalConfiguration>.Instance?.ArtilleryShelling;
            if (!_hostReady || world == null || globals?.ArtilleryMapsConfigs == null ||
                !globals.ArtilleryMapsConfigs.TryGetValue(world.LocationId, out ArtilleryShellingMapConfiguration map))
            {
                _waitingPeers[peer] = recipientId;
                return;
            }

            if (world.ServerShellingController?._currentMapConfiguration.ShellingZones != null)
            {
                map = world.ServerShellingController._currentMapConfiguration;
            }

            // Clients need zone definitions for alarms, not the host's impact grid
            map.ShellingZones = (ArtilleryShellingZone[])map.ShellingZones.Clone();
            for (int i = 0; i < map.ShellingZones.Length; i++)
            {
                map.ShellingZones[i].PointsWorldPositions = Array.Empty<Vector3>();
            }

            var snapshot = new RaidSnapshot
            {
                Location = world.LocationId, Map = map, Explosion = globals.ProjectileExplosionParams,
                Rules = CoopSupport.GetLocalRules(), Warnings = _warnings
                    .Where(entry => CoopSupport.WarnEveryone || entry.Value.Contains(recipientId))
                    .Select(entry => entry.Key).ToArray()
            };

            Send(new MortairPacket { Kind = PacketKind.Settings, Text = WireJson.Serialize(snapshot) }, peer);
            foreach (MortairPacket eye in _eyeStates.Values)
            {
                Send(eye, peer);
            }

            foreach (var alert in _alerts)
            {
                Send(new MortairPacket
                {
                    Kind = PacketKind.Alert, NetId = alert.Key,
                    Text = alert.Value.Zone, Position = alert.Value.Point
                }, peer);
            }

            _waitingPeers.Remove(peer);
        }

        private void ApplySettings(string json)
        {
            RaidSnapshot snapshot = WireJson.Deserialize<RaidSnapshot>(json);
            GameWorld world = Singleton<GameWorld>.Instance;
            if (snapshot == null || world == null || snapshot.Location != world.LocationId
                || snapshot.Map.ShellingZones == null || snapshot.Explosion == null || snapshot.Rules == null)
            {
                throw new InvalidOperationException("Invalid Mortair raid settings.");
            }

            var globals = Singleton<GlobalConfiguration>.Instance.ArtilleryShelling;
            globals.ProjectileExplosionParams = snapshot.Explosion;
            globals.ArtilleryMapsConfigs[world.LocationId] = snapshot.Map;
            world.ClientShellingController._currentMapConfiguration = snapshot.Map;

            CoopSupport.HostRules = snapshot.Rules;
            bool first = !_ready;
            _ready = true;

            if (first && snapshot.Warnings != null)
            {
                foreach (string zone in snapshot.Warnings)
                {
                    GlobalEventsController.CreateEvent<ShellingNotifyEvent>()
                        .Invoke(new[] { world.MainPlayer.PlayerId }, zone);
                }
            }

            Logger.LogDebug("[Mortair] host raid settings applied.");
        }

        private void OnWarning(ShellingNotifyEvent ev)
        {
            if (!FikaBackendUtils.IsServer || !CoopSupport.Enabled)
            {
                return;
            }

            _warnings[ev.ZoneId] = (ev.PlayersIDs ?? Array.Empty<int>()).Select(FindPlayerId)
                .Where(player => player != null).Select(player => player.NetId).ToArray();
            if (CoopSupport.WarnEveryone)
            {
                Send(new MortairPacket { Kind = PacketKind.Warning, Flag = true, Text = ev.ZoneId });
            }
            else if (ev.PlayersIDs != null)
            {
                foreach (int id in ev.PlayersIDs)
                {
                    FikaPlayer player = FindPlayerId(id);
                    if (player != null)
                    {
                        Send(new MortairPacket { Kind = PacketKind.Warning, NetId = player.NetId, Text = ev.ZoneId });
                    }
                }
            }
        }

        private void OnZoneEnded(LastShellingExplosionZoneEvent ev)
        {
            if (FikaBackendUtils.IsServer)
            {
                _warnings.Remove(ev.ZoneId);
            }
        }

        private void OnAlert(ChangePlayerShellingAlertStateEvent ev)
        {
            if (!FikaBackendUtils.IsServer || !CoopSupport.Enabled)
            {
                return;
            }
            
            FikaPlayer player = FindPlayerId(ev.PlayerId);
            if (player == null)
            {
                return;
            }
            
            if (_alerts.TryGetValue(player.NetId, out AlertState last) && last.Zone == ev.ZoneId)
            {
                if ((last.Point - ev.NearestPoint).sqrMagnitude < 0.01f || Time.unscaledTime - last.Time < 0.25f)
                {
                    return;
                }
            }

            _alerts[player.NetId] = new AlertState { Zone = ev.ZoneId, Point = ev.NearestPoint, Time = Time.unscaledTime };
            Send(new MortairPacket { Kind = PacketKind.Alert, NetId = player.NetId, Text = ev.ZoneId, Position = ev.NearestPoint });
        }

        private void OnNotify(NotifyEvent ev)
        {
            if (!FikaBackendUtils.IsServer || ev.MessageType != NotifyEvent.EMessageType.ArtilleryBusy)
            {
                return;
            }

            FikaPlayer player = FindPlayerId(ev.ReceiverId);
            if (player != null)
            {
                Send(new MortairPacket { Kind = PacketKind.Busy, NetId = player.NetId });
            }
        }

        private void OnEyeChanged(bool holding)
        {
            if (!(Singleton<GameWorld>.Instance?.MainPlayer is FikaPlayer player))
            {
                return;
            }

            var packet = new MortairPacket { Kind = PacketKind.Eye, NetId = player.NetId, Flag = holding };
            if (FikaBackendUtils.IsServer)
            {
                _eyeStates[player.NetId] = packet;
            }

            Send(packet);
        }

        private void ApplyEye(MortairPacket packet)
        {
            _eyeStates[packet.NetId] = packet;
            FikaPlayer player = Find(packet.NetId);

            if (player != null && !player.IsYourPlayer)
            {
                EyeWarning.SetRemoteHold(player.ProfileId, packet.Flag);
            }
        }

        internal void ApplyPlayerState(FikaPlayer player)
        {
            if (!IsClient || player.IsAI)
            {
                return;
            }

            // InitObservedPlayer runs after Fika registers this NetId, before spawning its hands.
            if (_eyeStates.TryGetValue(player.NetId, out MortairPacket eye) && !player.IsYourPlayer)
            {
                EyeWarning.SetRemoteHold(player.ProfileId, eye.Flag);
            }

            if (_alerts.TryGetValue(player.NetId, out AlertState alert))
            {
                GlobalEventsController.CreateEvent<ChangePlayerShellingAlertStateEvent>()
                    .Invoke(player.PlayerId, alert.Zone, alert.Point);
            }
        }

        private struct AlertState
        {
            internal string Zone;
            internal Vector3 Point;
            internal float Time;
        }
    }

    internal sealed class RaidSnapshot
    {
        public string Location;
        public ArtilleryShellingMapConfiguration Map;
        public GlobalConfiguration.ArtilleryProjectileExplosionParams Explosion;
        public RaidRules Rules;
        public string[] Warnings;
    }
}