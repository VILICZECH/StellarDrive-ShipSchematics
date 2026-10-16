using System;

namespace ShipSchematics
{
    [Serializable]
    public class CableRecord
    {
        public int type;
        public int aPart;
        public int aSocket;
        public int bPart;
        public int bSocket;
    }

    // A cable between two ships of one schematic. Ship 0 is shipJson, ship i + 1 is childShipJson[i].
    public sealed class LinkRecord
    {
        public int type;
        public int aShip;
        public int aPart;
        public int aSocket;
        public int bShip;
        public int bPart;
        public int bSocket;
    }

    [Serializable]
    public class SchematicFile
    {
        public int version = 1;
        public string id;
        public string name;
        public string savedUtc;
        public int partCount;
        public uint[] shipIds;
        public uint[] shipSpaces;
        public string shipJson;
        public string[] childShipJson;
        public string[] childCableData;
        public string[] childPoses;
        public CableRecord[] cables;
        public string cableData;
        public string linkData;
        public string partNames;
        public string fingerprint;
    }

    public sealed class ImportOutcome
    {
        public SchematicFile File;
        public string Message;
        public ImportKind Kind;
        public bool MissingParts;
    }

    public enum ImportKind
    {
        Failed,
        Added,
        Already,
        Picture
    }

    [Serializable]
    public class SchematicPackage
    {
        public int version = 1;
        public string name;
        public string savedUtc;
        public int partCount;
        public string shipJson;
        public string[] childShipJson;
        public string[] childCableData;
        public string[] childPoses;
        public CableRecord[] cables;
        public string cableData;
        public string linkData;
        public string partNames;
        public string fingerprint;
        public string preview;
    }
}
