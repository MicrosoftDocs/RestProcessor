namespace Microsoft.RestApi.RestSplitter.Generator
{
    using System.Collections.Generic;
    using System.IO;

    using Microsoft.RestApi.Common;
    using Microsoft.RestApi.RestSplitter.Model;

    using Newtonsoft.Json.Linq;
    using System.Linq;
    using System;

    public abstract class BaseGenerator : IGenerator
    {
        protected Dictionary<string, Tuple<JObject, string>> keyValuePairs = new Dictionary<string, Tuple<JObject, string>>();
        protected JObject RootJObj { get; }

        protected string TargetDir { get; }

        protected string FilePath { get; }

        protected OrgsMappingFile OrgsMappingFile { get; }

        protected IDictionary<string, int> LineNumberMappingDict { get; }

        protected RepoFile RepoFile { get; }

        protected string SwaggerRelativePath { get; set; }

        protected string Version { get; set; }

        #region Constructors

        protected BaseGenerator(JObject rootJObj, string targetDir, string filePath, OrgsMappingFile orgsMappingFile, IDictionary<string, int> lineNumberMappingDict, RepoFile repoFile, string swaggerRelativePath, string version)
        {
            Guard.ArgumentNotNull(rootJObj, nameof(rootJObj));
            Guard.ArgumentNotNullOrEmpty(targetDir, nameof(targetDir));
            Guard.ArgumentNotNullOrEmpty(filePath, nameof(filePath));
            Guard.ArgumentNotNull(orgsMappingFile, nameof(orgsMappingFile));

            RootJObj = rootJObj;
            TargetDir = targetDir;
            FilePath = filePath;
            OrgsMappingFile = orgsMappingFile;
            LineNumberMappingDict = lineNumberMappingDict;
            RepoFile = repoFile;
            SwaggerRelativePath = swaggerRelativePath;
            Version = version;
        }


        #endregion

        #region Public Methods

        public abstract IEnumerable<FileNameInfo> Generate();
        public Dictionary<string, Tuple<JObject, string>> GetStoreInfo()
        {
            return keyValuePairs;
        }
        protected abstract string GetOperationName(JObject operation, out string operationId);

        #endregion

        #region Protected Methods

        protected static void ClearKey(JObject jObject, string key)
        {
            JToken obj;
            if (jObject.TryGetValue(key, out obj))
            {
                jObject.Remove(key);
            }
        }

        protected IEnumerable<FileNameInfo> GenerateOperations(JObject rootJObj, JObject paths, string targetDir, string groupName)
        {
            foreach (var path in paths)
            {
                foreach (var item in (JObject)path.Value)
                {
                    // Skip for parameters
                    if (item.Key.Equals("parameters"))
                    {
                        continue;
                    }

                    var operationObj = (JObject)item.Value;
                    var operationName = GetOperationName(operationObj, out string operationId);
                    var operationIdWithoutTag = RemoveTag(operationId, groupName);

                    var operationTocName = Utility.ExtractPascalNameByRegex(RemoveTag(operationName, groupName), OrgsMappingFile.NoSplitWords);
                    operationObj["x-internal-toc-name"] = operationTocName;

                    // Reuse the root object, to reuse the other properties
                    rootJObj["paths"] = new JObject
                    {
                        {
                            path.Key, new JObject
                            {
                                { item.Key, operationObj }
                            }
                        }
                    };

                    rootJObj["x-internal-split-type"] = SplitType.Operation.ToString();
                    rootJObj["x-internal-operation-id"] = operationIdWithoutTag;
                    rootJObj["x-internal-operation-name"] = operationTocName;

                    //Set metadata source_url
                    bool enableViewCode = false;
                    string swaggerSourceUrl = LineNumberMappingDict.TryGetValue(operationId, out int lineNumber) ? GetTheSwaggerSource(lineNumber, out enableViewCode) : GetTheSwaggerSource(1, out enableViewCode);

                    if (swaggerSourceUrl != null && enableViewCode)
                    {
                        rootJObj["x-internal-swagger-source-url"] = swaggerSourceUrl;
                    }

                    var groupNamePath = Utility.TryToFormalizeUrl(groupName, OrgsMappingFile.FormalizeUrl);
                    var legacyGroupNamePath = groupNamePath;
                    groupNamePath = Utility.ExtractPascalFileNameByRegex(groupNamePath, OrgsMappingFile.NoSplitWords, "-");
                    var operationNamePath = Utility.TryToFormalizeUrl(operationId, OrgsMappingFile.FormalizeUrl);
                    var operationFile = Utility.Serialize(Path.Combine(targetDir, groupNamePath), Utility.ExtractPascalFileNameByRegex(RemoveTag(operationNamePath, legacyGroupNamePath), OrgsMappingFile.NoSplitWords, "-"), rootJObj);
                    ClearKey(rootJObj, "x-internal-split-type");
                    ClearKey(rootJObj, "x-internal-operation-id");
                    ClearKey(rootJObj, "x-internal-operation-name");
                    ClearKey(rootJObj, "x-internal-swagger-source-url");

                    var fileName = Path.Combine(groupNamePath, operationFile.Item1);
                    yield return new FileNameInfo
                    {
                        TocName = operationTocName,
                        FileName = OrgsMappingFile.UseYamlSchema ? Path.ChangeExtension(fileName, "yml") : fileName,
                        FilePath = operationFile.Item2,
                        SwaggerSourceUrl = swaggerSourceUrl,
                        Version = Version
                    };
                }
            }
        }

        protected void MergePathParametersToOperations(JObject filteredPaths, Dictionary<string, JToken> pathsParameters)
        {
            foreach (var path in filteredPaths)
            {
                var pathParameters = pathsParameters.ContainsKey(path.Key) ? pathsParameters[path.Key] : null;
                foreach (var item in (JObject)path.Value)
                {
                    var operationObj = (JObject)item.Value;
                    JToken operationParameters;
                    if (operationObj.TryGetValue("parameters", out operationParameters))
                    {
                        JArray parameters = new JArray();
                        parameters.Merge(operationParameters);
                        if (pathParameters != null)
                        {
                            parameters.Merge(pathParameters);
                        }

                        operationObj["parameters"] = DistinctParameters(parameters);
                    }
                    else if (pathParameters != null)
                    {
                        operationObj["parameters"] = pathParameters;
                    }
                }
            }
        }

        protected string GetTheSwaggerSource(int lineNum, out bool enableViewCode)
        {
            SwaggerRelativePath = SwaggerRelativePath.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var paths = SwaggerRelativePath.Split(Path.AltDirectorySeparatorChar);
            enableViewCode = false;

            if (RepoFile != null && paths.Count() > 0)
            {
                var repo = RepoFile.Repos.SingleOrDefault(r => string.Equals(r.Name, paths[0], StringComparison.OrdinalIgnoreCase));
                if (repo != null)
                {
                    enableViewCode = repo.EnableViewCode;

                    if (repo.Url.Contains("github.com"))
                    {
                        return $"{repo.Url.TrimEnd('/')}/blob/{repo.Branch}/{SwaggerRelativePath.Substring(paths[0].Length + 1)}#L{lineNum}";
                    }
                    else if (repo.Url.Contains("visualstudio.com"))
                    {
                        return $"{repo.Url.TrimEnd('/')}?path={SwaggerRelativePath.Substring(paths[0].Length + 1)}&version=GB{repo.Branch}&a=contents&line={lineNum}";
                    }
                }
            }
            return null;
        }

        private JArray DistinctParameters(JArray parameters)
        {
            JArray distinctedParameters = new JArray();

            foreach (var p in parameters)
            {
                if (!distinctedParameters.Any(v => IsParameterEquals(v, p)))
                {
                    distinctedParameters.Add(p);
                }
            }

            return distinctedParameters;
        }

        private bool IsParameterEquals(JToken left, JToken right)
        {
            if (left["$ref"] != null && right["$ref"] != null)
            {
                return string.Equals(left["$ref"].ToString(), right["$ref"].ToString());
            }

            if (left["name"] != null && right["name"] != null && left["in"] != null && right["in"] != null)
            {
                return string.Equals(left["name"].ToString(), right["name"].ToString())
                    && string.Equals(left["in"].ToString(), right["in"].ToString());
            }
            return false;
        }

        private string RemoveTag(string operationName, string groupName)
        {
            Guard.ArgumentNotNullOrEmpty(operationName, nameof(operationName));
            Guard.ArgumentNotNullOrEmpty(groupName, nameof(groupName));

            var internalOperationName = operationName;
            if (OrgsMappingFile.IsGroupdedByTag && OrgsMappingFile.RemoveTagFromOperationId)
            {
                if (operationName.StartsWith(groupName))
                {
                    internalOperationName = operationName.TrimStart(groupName.ToCharArray());
                }
                internalOperationName = internalOperationName.Trim('_').Trim(' ');
            }
            return internalOperationName;
        }

        #endregion
    }
}
