using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ShipSchematics
{
    // QuickOutline.Outline.SmoothNormals averages the normals of vertices that share a position. The original reads
    // mesh.normals (a full array copy) inside its inner loop, and throws when a hull mesh has fewer normals than
    // vertices, which happened while blueprint holograms were created. This does the same in one pass: vertices at
    // the same exact position get their normalized normal sum, summed in vertex order like the original.
    [HarmonyPatch(typeof(Outline), "SmoothNormals")]
    static class OutlineSmoothNormalsGuard
    {
        static bool Prepare()
        {
            return AccessTools.Method(typeof(Outline), "SmoothNormals") != null;
        }

        static bool Prefix(Mesh mesh, ref List<Vector3> __result)
        {
            try
            {
                __result = Smooth(mesh);
                return false;
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning("Could not prepare an outline mesh. " + ex.Message);
                return true;
            }
        }

        static List<Vector3> Smooth(Mesh mesh)
        {
            if (mesh == null)
                return new List<Vector3>();
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            int count = vertices.Length;
            var result = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
                result.Add(i < normals.Length ? normals[i] : Vector3.up);

            var sums = new Dictionary<Vector3, Vector3>(count);
            var shared = new Dictionary<Vector3, int>(count);
            for (int i = 0; i < count; i++)
            {
                Vector3 position = vertices[i];
                sums.TryGetValue(position, out Vector3 sum);
                sums[position] = sum + result[i];
                shared.TryGetValue(position, out int seen);
                shared[position] = seen + 1;
            }
            for (int i = 0; i < count; i++)
            {
                if (shared.TryGetValue(vertices[i], out int seen) && seen > 1)
                    result[i] = sums[vertices[i]].normalized;
            }
            return result;
        }
    }
}
