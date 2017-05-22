﻿namespace RestProcessor
{
    using System;
    using System.Collections.Generic;

    using Newtonsoft.Json;

    [Serializable]
    public class OrgsMappingFile
    {
        [JsonProperty("target_api_root_dir")]
        public string TargetApiRootDir { get; set; }

        [JsonProperty("auto_generate_apis_page")]
        public bool AutoGenerateApisPage { get; set; }

        [JsonProperty("organizations")]
        public List<OrgInfo> OrgInfos { get; set; }
    }
}
