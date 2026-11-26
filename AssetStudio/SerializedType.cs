using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    public class SerializedType
    {
        public int classID;
        public bool m_IsStrippedType;
        public short m_ScriptTypeIndex = -1;
        public TypeTree? m_Type;
        public byte[]? m_ScriptID; //Hash128
        public byte[]? m_OldTypeHash; //Hash128
        public byte[]? m_TypeTreeHash; //Hash128 of the TypeTree data, format 23 and up
        public int[]? m_TypeDependencies;
        public string? m_KlassName;
        public string? m_NameSpace;
        public string? m_AsmName;
    }
}
