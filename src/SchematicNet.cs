using System;
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
    public struct SchematicPlaceRequest : IBroadcast
    {
        public uint SpaceId;
        public Vector3 Position;
        public Quaternion Rotation;
        public string Json;
    }

    public struct SchematicPlaceResult : IBroadcast
    {
        public bool Ok;
        public string Message;
    }

    // A client cannot LoadShip. Their own schematic is sent to the host, who builds it in the world.
    // Each player still keeps their own library; this does not share the host's list.
    static class SchematicNet
    {
        const int MaxJson = 8 * 1024 * 1024;

        static NetworkManager _manager;
        static Action<bool, string> _pending;
        static float _pendingUntil;
        static bool _busy;

        static SchematicNet()
        {
            GenericWriter<SchematicPlaceRequest>.SetWrite(WriteRequest);
            GenericReader<SchematicPlaceRequest>.SetRead(ReadRequest);
            GenericWriter<SchematicPlaceResult>.SetWrite(WriteResult);
            GenericReader<SchematicPlaceResult>.SetRead(ReadResult);
        }

        public static void Tick()
        {
            if (NetworkManager.Instances.Count == 0)
                return;
            Ensure();
            if (_busy && Time.unscaledTime > _pendingUntil)
            {
                _busy = false;
                Action<bool, string> done = _pending;
                _pending = null;
                if (done != null)
                    done(false, "The host did not place the ship.");
            }
        }

        public static bool Request(SchematicFile file, uint spaceId, Vector3 point, Quaternion rotation, Action<bool, string> done)
        {
            Ensure();
            if (NetworkManager.Instances.Count == 0 || !InstanceFinder.IsClientStarted)
                return false;
            if (_busy)
            {
                if (done != null)
                    done(false, "Another ship is already being placed.");
                return false;
            }
            string json = SchematicLibrary.ToNetJson(file);
            if (string.IsNullOrEmpty(json) || json.Length > MaxJson)
                return false;
            _busy = true;
            _pending = done;
            _pendingUntil = Time.unscaledTime + 20f;
            InstanceFinder.ClientManager.Broadcast(new SchematicPlaceRequest
            {
                SpaceId = spaceId,
                Position = point,
                Rotation = rotation,
                Json = json
            }, Channel.Reliable);
            return true;
        }

        static void Ensure()
        {
            NetworkManager manager = InstanceFinder.NetworkManager;
            if (manager == null || manager == _manager)
                return;
            _manager = manager;
            manager.ServerManager.RegisterBroadcast<SchematicPlaceRequest>(OnRequest, false);
            manager.ClientManager.RegisterBroadcast<SchematicPlaceResult>(OnResult);
        }

        static void OnRequest(NetworkConnection connection, SchematicPlaceRequest message, Channel channel)
        {
            string text = "Could not place the ship.";
            bool ok = false;
            try
            {
                if (!InstanceFinder.IsServerStarted)
                    text = "Only the host can place a ship.";
                else if (message.Json == null || message.Json.Length == 0 || message.Json.Length > MaxJson)
                    text = "That ship is too large to send.";
                else
                {
                    SchematicFile file = SchematicLibrary.FromNetJson(message.Json);
                    ok = Subships.Place(file, message.SpaceId, message.Position, message.Rotation, out text, false);
                    if (ok)
                        MelonLogger.Msg("Placed " + (file != null && !string.IsNullOrEmpty(file.name) ? file.name : "a ship") + " for a player.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not place a player's ship. " + ex.Message);
                text = "Could not place the ship.";
                ok = false;
            }
            if (connection == null || !InstanceFinder.IsServerStarted)
                return;
            InstanceFinder.ServerManager.Broadcast(connection, new SchematicPlaceResult { Ok = ok, Message = text ?? "" }, false, Channel.Reliable);
        }

        static void OnResult(SchematicPlaceResult message, Channel channel)
        {
            _busy = false;
            Action<bool, string> done = _pending;
            _pending = null;
            if (done != null)
                done(message.Ok, message.Message);
            else if (!string.IsNullOrEmpty(message.Message))
                ScreenMessages.Status(message.Message);
        }

        static void WriteRequest(Writer writer, SchematicPlaceRequest value)
        {
            writer.WriteUInt32(value.SpaceId);
            writer.WriteVector3Unpacked(value.Position);
            writer.WriteQuaternionUnpacked(value.Rotation);
            writer.WriteString(value.Json ?? "");
        }

        static SchematicPlaceRequest ReadRequest(Reader reader)
        {
            return new SchematicPlaceRequest
            {
                SpaceId = reader.ReadUInt32(),
                Position = reader.ReadVector3Unpacked(),
                Rotation = reader.ReadQuaternionUnpacked(),
                Json = reader.ReadStringAllocated()
            };
        }

        static void WriteResult(Writer writer, SchematicPlaceResult value)
        {
            writer.WriteBoolean(value.Ok);
            writer.WriteString(value.Message ?? "");
        }

        static SchematicPlaceResult ReadResult(Reader reader)
        {
            return new SchematicPlaceResult
            {
                Ok = reader.ReadBoolean(),
                Message = reader.ReadStringAllocated()
            };
        }
    }
}
