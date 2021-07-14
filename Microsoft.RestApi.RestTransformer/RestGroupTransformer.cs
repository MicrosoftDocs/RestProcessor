namespace Microsoft.RestApi.RestTransformer
{
    using System.Collections.Concurrent;
    using System.Linq;

    using Microsoft.DocAsCode.Build.RestApi.Swagger;
    using Microsoft.DocAsCode.DataContracts.RestApi;
    using Microsoft.RestApi.RestTransformer.Models;

    public abstract class RestGroupTransformer
    {
        protected abstract string GetSummary(SwaggerModel swaggerModel, RestApiRootItemViewModel viewModel);
        
        public OperationGroupEntity Transform(SwaggerModel swaggerModel, RestApiRootItemViewModel viewModel, ConcurrentDictionary<string, ConcurrentBag<Operation>> groupOperations, string version)
        {
            var serviceId = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-service-id");
            var serviceName = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-service-name");
            var groupName = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-toc-name");
            var subgroupName = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-sub-group-name");
            var productUid = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-product-uid");

            var basePath = swaggerModel.BasePath;
            var apiVersion = swaggerModel.Info.Version;

            var groupId = Utility.TrimUId($"{Utility.GetHostWithBasePathUId(swaggerModel.Host, productUid, basePath)}.{serviceId}.{subgroupName}.{groupName}")?.ToLower();

            ConcurrentBag<Operation> operations;
            var key = string.IsNullOrEmpty(version) ? groupId : $"{version}_{groupId}";
            if (groupOperations.TryGetValue(key, out operations))
            {
                if (operations.Count() > 0)
                {
                    var summary = GetSummary(swaggerModel, viewModel);
                    var metadataDesc = Utility.ExtractMetaDataDescription(summary, serviceName);
                    if (string.IsNullOrEmpty(summary)) {
                        metadataDesc = GenerateMetaDataDescription(operations, serviceName, groupName);
                    }

                    return new OperationGroupEntity
                    {
                        Id = groupId,
                        ApiVersion = apiVersion,
                        Name = groupName,
                        Operations = operations.OrderBy(p => p.Id).ToList(),
                        Service = serviceName,
                        Summary = summary,
                        Metadata = new MetaDataEntity() {
                            Description = metadataDesc
                        }
                    };
                }
            }
            
            return null;
        }

        private string GenerateMetaDataDescription(ConcurrentBag<Operation> operations, string serviceName, string groupName)
        {
            const string formatStr= "Learn more about [{0} {1} Operations]. How to [{2}].";
            var names=operations.Select(p=>p.Name).OrderBy(n => n);
            return string.Format(formatStr, serviceName, groupName, string.Join(",", names));
        }
    }
}
