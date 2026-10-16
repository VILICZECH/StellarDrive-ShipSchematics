using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Serializing;
using FishNet.Transporting;
using MelonLoader;
using UnityEngine;

namespace ShipSchematics
{
    public struct ModListBroadcast : IBroadcast
    {
        public string mods;
    }

    // Host and players send each other their list of mods on join, and a difference is shown as a warning.
    static class ModMatch
    {
        const float AnswerSeconds = 8f;

        struct Pending
        {
            public int Id;
            public float Until;
        }

        static ModMatch()
        {
            GenericWriter<ModListBroadcast>.SetWrite((writer, value) => writer.WriteString(value.mods ?? ""));
            GenericReader<ModListBroadcast>.SetRead((reader) => new ModListBroadcast { mods = reader.ReadStringAllocated() });
        }

        static NetworkManager _manager;
        static readonly List<Pending> Waiting = new List<Pending>();
        static bool _clientWaiting;
        static float _clientUntil;

        public static void Reset()
        {
            Waiting.Clear();
            _clientWaiting = false;
        }

        public static void Tick()
        {
            // InstanceFinder.NetworkManager writes a log line on every read while no NetworkManager exists, which is
            // the whole time in the main menu.
            if (NetworkManager.Instances.Count == 0)
                return;
            Ensure();
            float now = Time.unscaledTime;
            for (int i = Waiting.Count - 1; i >= 0; i--)
            {
                if (now < Waiting[i].Until)
                    continue;
                Waiting.RemoveAt(i);
                Warn("A player joined without the same mods. Their mod list never arrived.");
            }
            if (!_clientWaiting || now < _clientUntil)
                return;
            _clientWaiting = false;
            if (!InstanceFinder.IsServerStarted)
                Warn("The host is not running the same mods. No mod list came back.");
        }

        static void Ensure()
        {
            NetworkManager manager = InstanceFinder.NetworkManager;
            if (manager == null || _manager == manager)
                return;
            _manager = manager;
            manager.ServerManager.RegisterBroadcast<ModListBroadcast>(OnClientList, false);
            manager.ClientManager.RegisterBroadcast<ModListBroadcast>(OnHostList);
            manager.ServerManager.OnRemoteConnectionState += OnRemote;
            manager.ClientManager.OnClientConnectionState += OnClientState;
            manager.ClientManager.OnAuthenticated += SendOurs;
            if (InstanceFinder.IsClientStarted)
                SendOurs();
        }

        static void SendOurs()
        {
            if (!InstanceFinder.IsClientStarted)
                return;
            InstanceFinder.ClientManager.Broadcast(new ModListBroadcast { mods = LocalList() }, Channel.Reliable);
            if (!InstanceFinder.IsServerStarted)
            {
                _clientWaiting = true;
                _clientUntil = Time.unscaledTime + AnswerSeconds;
            }
        }

        static void OnRemote(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (connection == null || connection.IsLocalClient)
                return;
            if (args.ConnectionState == RemoteConnectionState.Started)
                Waiting.Add(new Pending { Id = connection.ClientId, Until = Time.unscaledTime + AnswerSeconds });
            else
                Forget(connection.ClientId);
        }

        static void OnClientState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Stopped)
                _clientWaiting = false;
        }

        static void OnClientList(NetworkConnection connection, ModListBroadcast message, Channel channel)
        {
            if (connection != null)
                Forget(connection.ClientId);
            string difference = Difference(LocalList(), message.mods);
            if (difference != null)
                Warn("Mod mismatch with a player. " + difference);
            if (connection != null && InstanceFinder.IsServerStarted)
                InstanceFinder.ServerManager.Broadcast(connection, new ModListBroadcast { mods = LocalList() }, false, Channel.Reliable);
        }

        static void OnHostList(ModListBroadcast message, Channel channel)
        {
            _clientWaiting = false;
            if (InstanceFinder.IsServerStarted)
                return;
            string difference = Difference(LocalList(), message.mods);
            if (difference != null)
                Warn("Mod mismatch with the host. " + difference);
        }

        static void Forget(int clientId)
        {
            for (int i = Waiting.Count - 1; i >= 0; i--)
            {
                if (Waiting[i].Id == clientId)
                    Waiting.RemoveAt(i);
            }
        }

        // Mods only: MelonLoader plugins are loader tools, not content both sides need.
        static string LocalList()
        {
            var lines = new List<string>();
            foreach (MelonMod mod in MelonMod.RegisteredMelons)
            {
                if (mod == null || mod.Info == null || string.IsNullOrEmpty(mod.Info.Name))
                    continue;
                lines.Add(mod.Info.Name + " " + mod.Info.Version);
            }
            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines);
        }

        static string Difference(string local, string remote)
        {
            var here = Split(local);
            var there = Split(remote);
            if (here.SetEquals(there))
                return null;
            return "Only here: " + List(here, there) + ". Only there: " + List(there, here) + ".";
        }

        static HashSet<string> Split(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text))
                return set;
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                    set.Add(trimmed);
            }
            return set;
        }

        static string List(HashSet<string> include, HashSet<string> exclude)
        {
            var names = new List<string>();
            foreach (string line in include)
            {
                if (!exclude.Contains(line))
                    names.Add(line);
            }
            names.Sort(StringComparer.Ordinal);
            if (names.Count == 0)
                return "nothing";
            if (names.Count > 4)
                return string.Join(", ", names.GetRange(0, 4)) + ", ...";
            return string.Join(", ", names);
        }

        static void Warn(string message)
        {
            MelonLogger.Warning(message);
            ScreenMessages.Warning(message);
        }
    }
}
