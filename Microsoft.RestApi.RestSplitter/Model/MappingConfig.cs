﻿namespace Microsoft.RestApi.RestSplitter.Model
{
    public class MappingConfig
    {
        public bool IsOperationLevel { get; set; }

        public bool IsGroupedByTag { get; set; }

        public int SplitOperationCountGreaterThan { get; set; }

        public bool UseYamlSchema { get; set; }

        public bool RemoveTagFromOperationId { get; set; }

        public bool NeedResolveXMsPaths { get; set; }

        public bool UseServiceUrlGroup { get; set; }

        public bool GenerateSourceUrl { get; set; }
    }
}
