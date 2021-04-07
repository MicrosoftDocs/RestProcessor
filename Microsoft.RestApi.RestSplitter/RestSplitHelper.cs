﻿namespace Microsoft.RestApi.RestSplitter
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using Microsoft.RestApi.RestSplitter.Generator;
    using Microsoft.RestApi.RestSplitter.Model;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public static class RestSplitHelper
    {
        public static RestFileInfo Split(string targetDir, string filePath, string swaggerRelativePath, string serviceId, string serviceName, string subGroupName, OperationGroupMapping operationGroupMapping, OrgsMappingFile orgsMappingFile, RepoFile repoFile, string version, RestAcrossSwaggerSplitter restAcrossSwaggerSplitter)
        {
            
            if (!Directory.Exists(targetDir))
            {
                throw new ArgumentException($"{nameof(targetDir)} '{targetDir}' should exist.");
            }
            if (!File.Exists(filePath))
            {
                throw new ArgumentException($"{nameof(filePath)} '{filePath}' should exist.");
            }

            var sourceSwaggerFilePath = Utility.GetSourceSwaggerFilePath(filePath);
            IDictionary<string, int> lineNumberMappingDict = new Dictionary<string, int>();

            using (var streamReader = File.OpenText(sourceSwaggerFilePath))
            {
                using (var reader = new JsonTextReader(streamReader))
                {
                    var rootJObj = JObject.Load(reader);
                    lineNumberMappingDict = GetLineNumberMappingInfo(rootJObj);
                }
            }

            var restFileInfo = new RestFileInfo();

            using (var streamReader = File.OpenText(filePath))
            {
                using (var reader = new JsonTextReader(streamReader))
                {
                    reader.DateParseHandling = DateParseHandling.None;

                    var rootJObj = JObject.Load(reader);

                    // Resolve $ref with json file instead of definition reference in the same swagger
                    var refResolver = new RefResolver(rootJObj, filePath);
                    refResolver.Resolve();

                    if (orgsMappingFile.NeedResolveXMsPaths)
                    {
                        var xMsPathsResolver = new XMsPathsResolver(rootJObj);
                        xMsPathsResolver.Resolve();
                    }

                    rootJObj["x-internal-service-id"] = serviceId;
                    rootJObj["x-internal-service-name"] = serviceName;
                    rootJObj["x-internal-sub-group-name"] = subGroupName;

                    var generator = GeneratorFactory.CreateGenerator(rootJObj, targetDir, filePath, operationGroupMapping, orgsMappingFile, lineNumberMappingDict, repoFile, swaggerRelativePath, version);
                    var fileNameInfos = generator.Generate().ToList();
                    restAcrossSwaggerSplitter.Merge(generator.GetStoreInfo());
                    if (fileNameInfos.Any())
                    {
                        restFileInfo.FileNameInfos = fileNameInfos;
                    }

                    restFileInfo.TocTitle = GetInfoTitle(rootJObj);
                }
            }
            return restFileInfo;
        }

        private static string GetInfoTitle(JObject root)
        {
            JToken info;
            if (root.TryGetValue("info", out info))
            {
                var infoJObj = (JObject)info;
                JToken title;
                if (infoJObj.TryGetValue("title", out title))
                {
                    return title.ToString();
                }
                throw new InvalidOperationException($"title is not defined in {infoJObj}");
            }
            throw new InvalidOperationException($"info is not defined in {root}");
        }

        private static IDictionary<string, int> GetLineNumberMappingInfo(JObject rootJObj)
        {
            var paths = (JObject)rootJObj["paths"];
            var xMsPaths = (JObject)rootJObj["x-ms-paths"];

            var lineNumberMappingDict = new Dictionary<string, int>();

            ParsePathJObjectInfo(paths, ref lineNumberMappingDict);
            ParsePathJObjectInfo(xMsPaths, ref lineNumberMappingDict);

            return lineNumberMappingDict;
        }

        private static void ParsePathJObjectInfo(JObject paths, ref Dictionary<string, int> lineNumberMappingDict)
        {
            if (paths == null)
            {
                return;
            }

            foreach (var path in paths)
            {
                foreach (var item in (JObject)path.Value)
                {
                    // Skip find tag for parameters
                    if (item.Key.Equals("parameters"))
                    {
                        continue;
                    }
                    if (((JObject)item.Value).TryGetValue("operationId", out JToken opId) && opId != null)
                    {
                        if (!lineNumberMappingDict.ContainsKey(opId.ToString()))
                        {
                            var lineNumber = (paths[path.Key][item.Key]["operationId"] as IJsonLineInfo).LineNumber;
                            lineNumberMappingDict.Add(opId.ToString(), lineNumber);
                        }
                    }
                }
            }
        }
    }
}
