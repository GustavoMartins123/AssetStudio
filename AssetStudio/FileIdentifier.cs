using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    public class FileIdentifier
    {
        public Guid guid;
        public int type; //enum { kNonAssetType = 0, kDeprecatedCachedAssetType = 1, kSerializedAssetType = 2, kMetaAssetType = 3 };
        public string pathName = string.Empty;

        //custom
        public string fileName = string.Empty;
    }
}
