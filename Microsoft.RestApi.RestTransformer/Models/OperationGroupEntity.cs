﻿namespace Microsoft.RestApi.RestTransformer.Models
{
    using System.Collections.Generic;

    using YamlDotNet.Serialization;

    public class OperationGroupEntity : NamedEntity
    {
        [YamlMember(Alias = "summary")]
        public string Summary { get; set; }

        [YamlMember(Alias = "apiVersion")]
        public string ApiVersion { get; set; }

        [YamlMember(Alias = "service")]
        public string Service { get; set; }

        [YamlMember(Alias = "metadata")]
        public MetaDataEntity Metadata { get; set; }

        [YamlMember(Alias = "operations")]
        public IList<Operation> Operations { get; set; }
    }

    public class Operation : IdentifiableEntity
    {
        [YamlIgnore]
        public string GroupId { get; set; }
        [YamlIgnore]
        public string Name { get; set; }

        [YamlMember(Alias = "summary")]
        public string Summary { get; set; }
    }
}
