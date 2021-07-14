namespace Microsoft.RestApi.RestTransformer
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Microsoft.DocAsCode.Build.RestApi.Swagger;
    using Microsoft.DocAsCode.DataContracts.RestApi;
    using Microsoft.RestApi.RestTransformer.Models;
    using MoreLinq;
    using Newtonsoft.Json.Linq;

    public class RestOperationTransformer
    {
        private static ConcurrentDictionary<string, IList<Definition>> _cacheDefinitions = new ConcurrentDictionary<string, IList<Definition>>();
        private static Regex _permissionRegex = new Regex("/providers(?<permission>(/[^/{}]+)+)(/{[^/}]+})*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static Regex _permissionRegexOfPost = new Regex("/providers(?<permission>(/[^/{}]+)+)(/{[^/}]+}).*(?<action>(/[^/{}]+){1})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static OperationEntity Transform(string groupKey, SwaggerModel swaggerModel, RestApiChildItemViewModel viewModel, bool needPermission = false)
        {
            var scheme = Utility.GetScheme(swaggerModel.Metadata);
            var hostWithParameters = Utility.GetHostWithParameters(swaggerModel.Host, swaggerModel.Metadata, viewModel.Metadata);

            var host = hostWithParameters.Host;
            var hostParameters = hostWithParameters.Parameters;
            if (!hostWithParameters.UseSchemePrefix)
            {
                scheme = string.Empty;
            }
            var apiVersion = Utility.GetApiVersion(viewModel, swaggerModel.Info.Version);

            var securities = TransformSecurities(viewModel, swaggerModel);
            IList<Definition> allDefinitions;
            if (!_cacheDefinitions.TryGetValue(groupKey, out allDefinitions))
            {
                allDefinitions = GetAllDefinitions(GetAllDefinitionObjects(swaggerModel));
                _cacheDefinitions.TryAdd(groupKey, allDefinitions);
            }

            var bodyDefinitionObject = new DefinitionObject();
            var parametersDefinitions = new List<Definition>();
            var allSimpleParameters = TransformAllSimpleParameters(securities, hostParameters, viewModel, ref bodyDefinitionObject, ref parametersDefinitions);

            var responseDefinitionObjects = new List<DefinitionObject>();
            var responses = TransformResponses(viewModel, allDefinitions, ref responseDefinitionObjects);
            var bodies = TransformRequestBodies(allDefinitions, allSimpleParameters.Where(p => p.ParameterEntityType == ParameterEntityType.Body).ToList(), bodyDefinitionObject);
            var typeToName = new Dictionary<string, string>();
            var referencedDefinitions = TransformDefinitions(allDefinitions, parametersDefinitions, bodyDefinitionObject, responseDefinitionObjects, out typeToName);

            UpdateDefinitions(referencedDefinitions, typeToName);
            UpdateBodies(bodies, typeToName);
            UpdateResponses(responses, typeToName);

            var basePath = swaggerModel.BasePath;
            var paths = TransformPaths(viewModel, scheme, host, basePath, apiVersion, allSimpleParameters);
            var serviceId = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-service-id");
            var serviceName = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-service-name");
            var groupName = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-toc-name");
            var subgroupName = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-sub-group-name");
            var operationId = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-operation-id");
            var operationName = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-operation-name");
            var sourceUrl = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-swagger-source-url");
            var productUid = swaggerModel.Metadata.GetValueFromMetaData<string>("x-internal-product-uid");
            var summary = Utility.GetSummary(viewModel.Summary, viewModel.Description);
            return new OperationEntity
            {
                Id = Utility.TrimUId($"{Utility.GetHostWithBasePathUId(swaggerModel.Host, productUid, basePath)}.{serviceId}.{subgroupName}.{groupName}.{operationId}")?.ToLower(),
                Name = operationName,
                Service = serviceName,
                Summary = summary,
                ApiVersion = apiVersion,
                GroupId = Utility.TrimUId($"{Utility.GetHostWithBasePathUId(swaggerModel.Host, productUid, basePath)}.{serviceId}.{subgroupName}.{groupName}")?.ToLower(),
                GroupName = groupName,
                IsDeprecated = swaggerModel.Metadata.GetValueFromMetaData<bool>("deprecated"),
                IsPreview = swaggerModel.Metadata.GetValueFromMetaData<bool>("x-ms-preview"),
                Responses = responses,
                Parameters = Helper.SortParameters(paths, allSimpleParameters.Where(p => p.ParameterEntityType == ParameterEntityType.Query || p.ParameterEntityType == ParameterEntityType.Path).ToList()),
                RequestHeaders = allSimpleParameters.Where(p => p.ParameterEntityType == ParameterEntityType.Header).ToList(),
                RequestBodies = bodies,
                Paths = Helper.HandlePathsDefaultValues(paths, apiVersion, allSimpleParameters.Where(p => p.ParameterEntityType == ParameterEntityType.Query || p.ParameterEntityType == ParameterEntityType.Path).ToList()),
                Produces = viewModel.Metadata.GetArrayFromMetaData<string>("produces"),
                Consumes = viewModel.Metadata.GetArrayFromMetaData<string>("consumes"),
                Examples = TransformExamples(viewModel, paths, allSimpleParameters, bodyDefinitionObject),
                Definitions = referencedDefinitions,
                Securities = securities,
                Permission = needPermission ? TransformPermission(viewModel, paths) : null,
                Metadata = TransformMetaData(sourceUrl, summary, serviceName),
                ErrorCodes = TransformErrorCodes(viewModel, swaggerModel)
            };
        }

        private static void UpdateResponses(IList<ResponseEntity> responses, Dictionary<string, string> typeToName)
        {
            if (responses == null) return;

            foreach(var response in responses)
            {
                UpdateParameter(response, typeToName);
            }
        }

        private static void UpdateBodies(IList<RequestBody> bodies, Dictionary<string, string> typeToName)
        {
            if (bodies == null) return;

            foreach(var body in bodies)
            {
                if (body == null || body.RequestBodyParameters == null) continue;

                foreach(var parameter in body.RequestBodyParameters)
                {
                    UpdateParameter(parameter, typeToName);
                }
            }
        }

        private static void UpdateDefinitions(IList<DefinitionEntity> referencedDefinitions, Dictionary<string, string> typeToName)
        {
            if (referencedDefinitions == null) return;

            foreach(var definition in referencedDefinitions)
            {
                if (definition == null || definition.ParameterItems == null) continue;

                foreach(var parameterItem in definition.ParameterItems)
                {
                    UpdateParameter(parameterItem, typeToName);
                }
            }
        }

        private static void UpdateParameter(BaseParameterEntity parameterItem, Dictionary<string, string> typeToName)
        {
            if (parameterItem == null) return;
            if (!string.IsNullOrEmpty(parameterItem.TypesTitle))
            {
                if (parameterItem.TypesTitle.EndsWith("[]"))
                {
                    var tempTitle = parameterItem.TypesTitle.Substring(0, parameterItem.TypesTitle.Length - 2);
                    if (typeToName.ContainsKey(tempTitle))
                    {
                        parameterItem.TypesTitle = typeToName[tempTitle] + "[]";
                    }
                }
                else
                {
                    if (typeToName.ContainsKey(parameterItem.TypesTitle))
                    {
                        parameterItem.TypesTitle = typeToName[parameterItem.TypesTitle];
                    }
                }
            }

            if (parameterItem.Types == null) return;

            foreach (var type in parameterItem.Types)
            {
                if (type == null || type.Id == null) continue;

                if (typeToName.ContainsKey(type.Id))
                {
                    type.Id = typeToName[type.Id];
                }
            }
        }

        #region Parameters

        private static IList<ParameterEntity> TransformAllSimpleParameters(IList<SecurityEntity> securities ,List<ParameterEntity> hostParameters, RestApiChildItemViewModel viewModel, ref DefinitionObject definitionObject, ref List<Definition> definitions)
        {
            var parameters = hostParameters == null ? new List<ParameterEntity>() : new List<ParameterEntity>(hostParameters);
            if (securities != null && securities.Count == 1 && securities.First().Type == "apiKey")
            {
                var security = securities.First();
                if (security.In != null && Enum.TryParse<ParameterEntityType>(security.In, true, out var parameterEntityType))
                {
                    parameters.Add(new ParameterEntity
                    {
                        Name = security.Name,
                        Description = security.Description,
                        IsRequired = true,
                        In = security.In,
                        ParameterEntityType = parameterEntityType,
                        Types = new List<BaseParameterTypeEntity>()
                        {
                            new BaseParameterTypeEntity{ Id = "string"}
                        }
                    });
                }
            }

            if (viewModel?.Parameters != null)
            {
                foreach (var parameter in viewModel.Parameters)
                {
                    var inType = parameter.Metadata.GetValueFromMetaData<string>("in");
                    if (inType != null && Enum.TryParse<ParameterEntityType>(inType, true, out var parameterEntityType))
                    {
                        var isRequired = parameter.Metadata.GetValueFromMetaData<bool>("required");
                        if (parameter.Metadata.TryGetValue("x-ms-required", out var msRequired))
                        {
                            isRequired = (bool)msRequired;
                        }

                        var skipUrlEncoding = parameterEntityType == ParameterEntityType.Path? parameter.Metadata.GetValueFromMetaData<bool>("x-ms-skip-url-encoding"): false;
                        var types = new List<BaseParameterTypeEntity>();
                       
                        if (parameter.Metadata.TryGetValue("type", out var type))
                        {
                            var enumValues = parameter.Metadata.GetArrayFromMetaData<string>("enum");
                            var enumNode = parameter.Metadata.GetDictionaryFromMetaData<Dictionary<string, object>>("x-ms-enum");
                            if (enumValues != null && enumNode != null)
                            {
                                var enumObjects = new List<EnumValue>();
                                foreach (var v in enumValues)
                                {
                                    enumObjects.Add(new EnumValue { Value = v });
                                }

                                var definition = new Definition
                                {
                                    DefinitionObjectType = DefinitionObjectType.Enum,
                                    Description = parameter.Description,
                                    EnumValues = enumObjects
                                };

                                if (enumNode.TryGetValue("name", out var enumName))
                                {
                                    types.Add(new BaseParameterTypeEntity
                                    {
                                        Id = (string)enumName
                                    });
                                    definition.Name = (string)enumName;
                                    definition.Type = (string)enumName;
                                }

                                if (enumNode.TryGetValue("values", out var enumValue))
                                {
                                    var values = (JArray)enumValue;
                                    if (values != null)
                                    {
                                        foreach (var v in values)
                                        {
                                            var keyValueEnum = v.ToObject<Dictionary<string, object>>();
                                            var enumV = keyValueEnum.GetValueFromMetaData<string>("value");
                                            var enumObject = enumObjects.FirstOrDefault(q => q.Value == enumV);
                                            if (enumV != null && enumObject != null)
                                            {
                                                enumObject.Description = keyValueEnum.GetValueFromMetaData<string>("description");
                                                var name = keyValueEnum.GetValueFromMetaData<string>("name");
                                                enumObject.Name = string.IsNullOrEmpty(name) ? enumV : name;
                                            }
                                        }
                                    }
                                }
                                definitions.Add(definition);
                            }
                            else
                            {
                                types.Add(new BaseParameterTypeEntity
                                {
                                    Id = (string)type
                                });
                            }

                            var parameterEntity = new ParameterEntity
                            {
                                Name = parameter.Name,
                                Description = parameter.Description,
                                IsRequired = isRequired,
                                SkipUrlEncoding = skipUrlEncoding,
                                Pattern = parameter.Metadata.GetValueFromMetaData<string>("pattern"),
                                Format = parameter.Metadata.GetValueFromMetaData<string>("format"),
                                In = inType,
                                ParameterEntityType = parameterEntityType,
                                Types = types,
                                EnumValues = enumValues
                            };
                            parameters.Add(parameterEntity);
                        }
                        else if (parameter.Metadata.TryGetValue("schema", out var schema))
                        {
                            definitionObject = ResolveSchema((JObject)schema);
                            definitionObject.Name = parameter.Name;
                            definitionObject.Description = parameter.Description;
                        }
                    }
                }
            }
            return parameters;
        }
        
        #endregion

        #region Paths

        private static string FormatPathQueryStrings(string initParameters, IEnumerable<ParameterEntity> queryParameters)
        {
            var queries = new List<string>();
            if (!string.IsNullOrEmpty(initParameters))
            {
                var initStrings = initParameters.Split('&').Select(p =>
                {
                    if (!queryParameters.Any(q => q.Name == p?.Split('=')[0]))
                    {
                        return p;
                    }
                    return null;
                });
                queries.AddRange(initStrings.Where(s => !string.IsNullOrEmpty(s)));
            }
            var queryStrings = queryParameters.Select(p =>
            {
                return $"{p.Name}={{{p.Name}}}";
            });
            queries.AddRange(queryStrings);
            return string.Join("&", queries);
        }

        private static IList<PathEntity> TransformPaths(RestApiChildItemViewModel viewModel, string scheme, string host, string basePath, string apiVersion, IList<ParameterEntity> parameters)
        {
            var pathEntities = new List<PathEntity>();

            var paths = viewModel.Path?.Split('?');
            var requiredQueryStrings = parameters.Where(p => p.IsRequired && p.In == "query");
            var requiredPath = paths[0];

            if (requiredQueryStrings.Any())
            {
                requiredPath = requiredPath + "?" + FormatPathQueryStrings(paths.Count() > 1 ? paths[1] : null, requiredQueryStrings);
            }

            pathEntities.Add(new PathEntity
            {
                Content = $"{viewModel.OperationName.ToUpper()} {Utility.ResolveScheme(scheme)}{Utility.GetHostWithBasePath(host, basePath)}{requiredPath}",
                IsOptional = false
            });

            var allQueryStrings = parameters.Where(p => p.In == "query");
            var optionPath = paths[0];
            if (!allQueryStrings.All(p => p.IsRequired))
            {
                optionPath = optionPath + "?" + FormatPathQueryStrings(paths.Count() > 1 ? paths[1] : null, allQueryStrings);

                pathEntities.Add(new PathEntity
                {
                    Content = $"{viewModel.OperationName.ToUpper()} {Utility.ResolveScheme(scheme)}{Utility.GetHostWithBasePath(host, basePath)}{optionPath}",
                    IsOptional = true
                });
            }
            return pathEntities;
        }

        private static string TransformPermission(RestApiChildItemViewModel viewModel, IList<PathEntity> pathEntities)
        {
                var pathEntity = pathEntities.FirstOrDefault();
                if (pathEntity == null || string.IsNullOrEmpty(pathEntity.Content))
                {
                    return null;
                }

                var index = pathEntity.Content.ToLowerInvariant().LastIndexOf("/providers/");
                if (index == -1)
                {
                    return null;
                }

                var lastProvider = pathEntity.Content.Substring(index);
                index = lastProvider.IndexOf("?");
                if (index != -1)
                {
                    lastProvider = lastProvider.Substring(0, index);
                }

                if (viewModel.OperationName.ToUpperInvariant()== "POST")
                {
                    var matchOfPost = _permissionRegexOfPost.Match(lastProvider);
                    if (matchOfPost.Success)
                    {
                        return matchOfPost.Groups["permission"].Value.TrimStart('/') + matchOfPost.Groups["action"].Value+ "/action";
                    }

                    Console.WriteLine($"The path doesn't know how to get permission:{pathEntity.Content}");
                    return null;
                }

                var matches = _permissionRegex.Match(lastProvider);
                if (matches.Success)
                {
                    var permission = matches.Groups["permission"].Value.TrimStart('/');
                    switch (viewModel.OperationName.ToUpperInvariant())
                    {
                        case "GET":
                            permission = permission + "/read";
                            break;
                        case "PUT":
                        case "PATCH":
                            permission = permission + "/write";
                            break;
                        case "DELETE":
                            permission = permission + "/delete";
                            break;
                    } 
                   
                   return permission;
                }

            return null;
        }


        #endregion

        #region Requestbody

        public static IList<RequestBody> TransformRequestBodies(IList<Definition> allDefinitions, List<ParameterEntity> bodyParameters, DefinitionObject bodyDefinitionObject)
        {
            var bodies = new List<RequestBody>();

            if (!string.IsNullOrEmpty(bodyDefinitionObject?.Type))
            {
                var polymorphicDefinitions = GetPolymorphicDefinitions(allDefinitions, bodyDefinitionObject.Type);
                if (polymorphicDefinitions == null  || polymorphicDefinitions.Count == 0)
                {
                    var selfDefinition = GetSelfDefinition(allDefinitions, bodyDefinitionObject.Type);
                    if (selfDefinition != null && bodyDefinitionObject.DefinitionObjectType != DefinitionObjectType.Array)
                    {
                        var parameterEntities = GetDefinitionParameters(allDefinitions, selfDefinition, true);
                        bodyParameters.AddRange(parameterEntities);
                    }
                    else
                    {
                        bodyParameters.Add(new ParameterEntity
                        {
                            Name = bodyDefinitionObject.Name,
                            Description = bodyDefinitionObject.Description,
                            IsRequired = bodyDefinitionObject.IsRequired,
                            IsReadOnly = bodyDefinitionObject.IsReadOnly,
                            In = "body",
                            ParameterEntityType = ParameterEntityType.Body,
                            Pattern = bodyDefinitionObject.Pattern,
                            Format = bodyDefinitionObject.Format,
                            Types = new List<BaseParameterTypeEntity> { new BaseParameterTypeEntity { Id = bodyDefinitionObject.Type, IsArray = bodyDefinitionObject.DefinitionObjectType == DefinitionObjectType.Array } }
                        });
                    }
                    
                    if (bodyParameters.Count > 0)
                    {
                        bodies.Add(new RequestBody
                        {
                            Name = "default",
                            RequestBodyParameters = bodyParameters
                        });
                    }
                }
                else
                {
                    foreach (var polymorphicDefinition in polymorphicDefinitions)
                    {
                        var fullBodyParameters = GetDefinitionParameters(allDefinitions, polymorphicDefinition, true);
                        foreach (var bodyParameter in bodyParameters)
                        {
                            fullBodyParameters.Add(Utility.Clone(bodyParameter));
                        }
                        if (fullBodyParameters.Count > 0)
                        {
                            bodies.Add(new RequestBody
                            {
                                Name = polymorphicDefinition?.Type,
                                Description = polymorphicDefinition?.Description,
                                RequestBodyParameters = fullBodyParameters
                            });
                        }
                    }
                }
            }

            return bodies.Count > 0 ? bodies : null;
        }

        #endregion RequestBody

        #region Responses

        private static IList<ResponseEntity> TransformResponses(RestApiChildItemViewModel child, IList<Definition> definitions, ref List<DefinitionObject> definitionObjects)
        {
            var responses = new List<ResponseEntity>();
            foreach (var response in child.Responses)
            {
                var typesName = new List<BaseParameterTypeEntity>();
                string typesTitle = null;
                var schema = response.Metadata.GetDictionaryFromMetaData<Dictionary<string, object>>("schema");
                if (schema != null)
                {
                    var definitionObject = ResolveSchema(response.Metadata.GetValueFromMetaData<JObject>("schema"));
                    //todo: will resolve responses and parameters
                    definitionObject.Type = string.IsNullOrEmpty(definitionObject.Type) ? response.Metadata.GetValueFromMetaData<string>("x-internal-ref-name") : definitionObject.Type;
                    definitionObjects.Add(definitionObject);

                    if (string.IsNullOrEmpty(definitionObject.DiscriminatorKey) && !string.IsNullOrEmpty(definitionObject.Type))
                    {
                        typesName.Add(new BaseParameterTypeEntity
                        {
                            IsArray = definitionObject.DefinitionObjectType == DefinitionObjectType.Array,
                            Id = definitionObject.Type
                        });
                    }
                    else if (!string.IsNullOrEmpty(definitionObject.DiscriminatorKey) && !string.IsNullOrEmpty(definitionObject.Type))
                    {
                        var polymorphicDefinitions = GetPolymorphicDefinitions(definitions, definitionObject.Type);
                        typesTitle = definitionObject.Type;
                        if (polymorphicDefinitions?.Count > 0)
                        {
                            foreach (var polymorphicDefinition in polymorphicDefinitions)
                            {
                                typesName.Add(new BaseParameterTypeEntity
                                {
                                    IsArray = definitionObject.DefinitionObjectType == DefinitionObjectType.Array,
                                    Id = polymorphicDefinition.Type
                                });
                            }
                        }
                    }
                }

                var headerList = new List<ResponseHeader>();
                var headers = response.Metadata.GetDictionaryFromMetaData<Dictionary<string, object>>("headers");
                if (headers != null)
                {
                    foreach(var header in headers)
                    {
                        var headerValue = ((JObject)header.Value).ToObject<Dictionary<string, object>>();
                        headerList.Add(new ResponseHeader
                        {
                            Name = header.Key,
                            Value = headerValue?.GetValueFromMetaData<string>("type")
                        });
                    }
                }

                responses.Add(new ResponseEntity
                {
                    Name = Utility.GetStatusCodeString(response.HttpStatusCode),
                    Description = response.Description,
                    Types = typesName.Count == 0 ? null : typesName,
                    TypesTitle = typesTitle,
                    ResponseHeaders = headerList.Count == 0 ? null : headerList
                });
            }

            return responses;
        }

        #endregion

        #region Example

        private static string GetExampleRequestUri(IList<PathEntity> paths, Dictionary<string, object> msExampleParameters, IList<ParameterEntity> parameters)
        {
            var pathContent = Helper.GetOptionalFullPath(paths);
            if (msExampleParameters != null)
            {
                foreach (var parameter in msExampleParameters)
                {
                    if (pathContent != null && pathContent.Contains($"{{{parameter.Key}}}"))
                    {
                        pathContent = pathContent?.Replace($"{{{parameter.Key}}}", Convert.ToString(parameter.Value));
                    }
                }
            }
            if (parameters != null)
            {
                foreach (var parameter in parameters)
                {
                    if (!parameter.IsRequired && pathContent != null && pathContent.Contains($"{{{parameter.Name}}}"))
                    {
                        pathContent = pathContent?.Replace($"/{{{parameter.Name}}}", string.Empty);
                    }
                }
            }
            if (pathContent.Contains('?'))
            {
                var contents = pathContent.Split('?');
                var queries = contents[1].Split('&');
                contents[1] = string.Join("&", queries.Where(q => !q.Contains("={")));
                if (string.IsNullOrEmpty(contents[1]))
                {
                    pathContent = contents[0];
                }
                else
                {
                    pathContent = string.Join("?", contents);
                }
            }

            return pathContent;
        }

        private static IList<ExampleRequestHeaderEntity> GetExampleRequestHeader(Dictionary<string, object> msExampleParameters, IList<ParameterEntity> headerParameters)
        {
            var exampleRequestHeaders = new List<ExampleRequestHeaderEntity>();

            foreach (var headerParameter in headerParameters)
            {
                if (msExampleParameters != null)
                {
                    foreach (var msExampleParameter in msExampleParameters)
                    {
                        if (msExampleParameter.Key == headerParameter.Name)
                        {
                            exampleRequestHeaders.Add(new ExampleRequestHeaderEntity
                            {
                                Name = msExampleParameter.Key,
                                Value = msExampleParameter.Value?.ToString()
                            });
                        }
                    }
                }
            }

            return exampleRequestHeaders.Count > 0 ? exampleRequestHeaders : null;
        }

        private static string GetExampleRequestBody(Dictionary<string, object> msExampleParameters, IList<ParameterEntity> bodyParameters, DefinitionObject bodyDefinitionObject)
        {
            if (msExampleParameters != null)
            {
                foreach (var msExampleParameter in msExampleParameters)
                {
                    if (msExampleParameter.Key == bodyDefinitionObject.Name)
                    {
                        return Utility.FormatJsonString(msExampleParameter.Value);
                    }
                }
            }

            foreach (var bodyParameter in bodyParameters)
            {
                if (msExampleParameters != null)
                {
                    foreach (var msExampleParameter in msExampleParameters)
                    {
                        if (msExampleParameter.Key == bodyParameter.Name || msExampleParameter.Key == "parameters")
                        {
                            return Utility.FormatJsonString(msExampleParameter.Value);
                        }
                    }
                }
            }

            return null;
        }

        private static List<ExampleResponseEntity> GetExampleResponses(Dictionary<string, object> msExampleResponses)
        {
            var exampleResponses = new List<ExampleResponseEntity>();
            if (msExampleResponses != null)
            {
                foreach (var msExampleResponse in msExampleResponses)
                {
                    var msExampleResponseValue = ((JObject)msExampleResponse.Value).ToObject<Dictionary<string, object>>();
                    string body = null;
                    if (msExampleResponseValue.TryGetValue("body", out var msBody) && msBody != null)
                    {
                        body = Utility.FormatJsonString(msBody);
                    }
                    else if (msExampleResponseValue.TryGetValue("value", out var msValue) && msValue != null)
                    {
                        body = Utility.FormatJsonString(msExampleResponseValue);
                    }

                    var headers = new List<ExampleResponseHeaderEntity>();
                    if (msExampleResponseValue.TryGetValue("headers", out var msHeader) && msHeader != null)
                    {
                        var msHeaderDict = ((JObject)msHeader).ToObject<Dictionary<string, string>>();
                        foreach (var header in msHeaderDict)
                        {
                            headers.Add(new ExampleResponseHeaderEntity
                            {
                                Name = header.Key,
                                Value = header.Value
                            });
                        }
                    }

                    var exampleResponse = new ExampleResponseEntity
                    {
                        StatusCode = msExampleResponse.Key,
                        Body = string.IsNullOrEmpty(body) ? null : body,
                        Headers = headers.Count > 0 ? headers : null
                    };
                    exampleResponses.Add(exampleResponse);
                }
            }

            return exampleResponses;
        }

        private static IList<ExampleEntity> TransformExamples(RestApiChildItemViewModel viewModel, IList<PathEntity> paths, IList<ParameterEntity> parameters, DefinitionObject bodyDefinitionObject)
        {
            var examples = new List<ExampleEntity>();
            var msExamples = viewModel.Metadata.GetDictionaryFromMetaData<Dictionary<string, object>>("x-ms-examples");
            if (msExamples != null)
            {
                foreach (var msExample in msExamples)
                {
                    var msExampleValue = ((JObject)msExample.Value).ToObject<Dictionary<string, object>>();
                    var msExampleParameters = msExampleValue.GetDictionaryFromMetaData<Dictionary<string, object>>("parameters");
                    var msExampleResponses = msExampleValue.GetDictionaryFromMetaData<Dictionary<string, object>>("responses");

                    var example = new ExampleEntity
                    {
                        Name = msExample.Key,
                        ExampleRequest = new ExampleRequestEntity
                        {
                            RequestUri = GetExampleRequestUri(paths, msExampleParameters, parameters.Where(p => p.In == "path").ToList()),
                            Headers = GetExampleRequestHeader(msExampleParameters, parameters.Where(p => p.In == "header").ToList()),
                            RequestBody = GetExampleRequestBody(msExampleParameters, parameters.Where(p => p.In == "body").ToList(), bodyDefinitionObject),
                        },
                        ExampleResponses = GetExampleResponses(msExampleResponses)
                    };
                    examples.Add(example);
                }
            }
            return examples;
        }

        #endregion

        #region Definitions

        private static IList<DefinitionObject> GetAllDefinitionObjects(SwaggerModel swaggerModel)
        {
            var allDefinitionObjects = new List<DefinitionObject>();
            if (swaggerModel.Definitions != null)
            {
                var definitions = ((JObject)swaggerModel.Definitions).ToObject<Dictionary<string, JObject>>();
                foreach (var definition in definitions)
                {
                    var definitionObject = ResolveSchema(definition.Value);
                    definitionObject.Name = definition.Key;
                    definitionObject.Type = definition.Key;
                    var flattenDefinitionObjects = FlattenDefinitionObject(definitionObject);
                    foreach (var flattenDefinitionObject in flattenDefinitionObjects)
                    {
                        if (!string.IsNullOrEmpty(flattenDefinitionObject.Type)
                            && string.IsNullOrEmpty(flattenDefinitionObject.DiscriminatorKey)
                            && (flattenDefinitionObject.DefinitionObjectType == DefinitionObjectType.Object
                            || flattenDefinitionObject.DefinitionObjectType == DefinitionObjectType.Enum
                            || (flattenDefinitionObject.DefinitionObjectType == DefinitionObjectType.Array && flattenDefinitionObject.PropertyItems?.Count > 0)
                            || (flattenDefinitionObject.DefinitionObjectType == DefinitionObjectType.Array && flattenDefinitionObject.AllOfs?.Count > 0))
                            && !allDefinitionObjects.Any(d => d.Type == flattenDefinitionObject.Type))
                        {
                            allDefinitionObjects.Add(flattenDefinitionObject);
                        }
                    }
                    var indexOfDefinitionObject = allDefinitionObjects.FindIndex(d => d.Type == definitionObject.Type);
                    if (indexOfDefinitionObject > -1)
                    {
                        allDefinitionObjects[indexOfDefinitionObject] = definitionObject;
                    }
                }
            }

           
            return allDefinitionObjects;
        }

        private static IList<Definition> GetAllDefinitions(IList<DefinitionObject> definitionObjects)
        {
            var definitions = new List<Definition>();
            foreach (var definitionObject in definitionObjects)
            {
                var definition = new Definition
                {
                    Name = definitionObject.ShortType?? definitionObject.Type,
                    Title = definitionObject.Title,
                    SubTitle = definitionObject.SubTitle,
                    Description = definitionObject.Description,
                    SubDescription = definitionObject.SubDescription,
                    Type = definitionObject.Type,
                    ShortType = definitionObject.ShortType,
                    DefinitionObjectType = definitionObject.DefinitionObjectType,
                    DefinitionProperties = GetDefinitionProperties(definitionObject),
                    DiscriminatorValue = definitionObject.DiscriminatorValue,
                    EnumValues = definitionObject.EnumValues,
                    AllOfTypes = definitionObject.AllOfs?.Select(p => p.Type).ToList()
                };
                definitions.Add(definition);
            }

            return definitions;
        }

        private static IList<DefinitionProperty> GetDefinitionProperties(DefinitionObject definitionObject)
        {
            var definitionProperties = new List<DefinitionProperty>();

            foreach (var property in definitionObject.PropertyItems)
            {
                if (!definitionProperties.Any(p => p.Name == property.Name))
                {
                    var definitionProperty = new DefinitionProperty
                    {
                        Name = property.Name,
                        Title = property.Title,
                        SubTitle = property.SubTitle,
                        Description = property.Description,
                        SubDescription = property.SubDescription,
                        Type = property.Type,
                        AdditionalType = property.AdditionalType,
                        IsReadOnly = property.IsReadOnly,
                        IsRequired = property.IsRequired,
                        IsFlatten = property.IsFlatten,
                        DiscriminatorKey = property.DiscriminatorKey,
                        DiscriminatorValue = property.DiscriminatorValue,
                        DefinitionObjectType = property.DefinitionObjectType,
                        Pattern = property.Pattern,
                        Format = property.Format,
                        EnumValues = property.EnumValues,
                        ShortType = property.ShortType
                    };
                    definitionProperties.Add(definitionProperty);
                }
            }

            foreach (var allOf in definitionObject.AllOfs)
            {
                var tmpProperties = GetDefinitionProperties(allOf);
                foreach (var tmpProperty in tmpProperties)
                {
                    if (!definitionProperties.Any(d => d.Name == tmpProperty.Name))
                    {
                        definitionProperties.Add(tmpProperty);
                    }
                }
            }

            return definitionProperties;
        }

        private static Definition GetSelfDefinition(IList<Definition> allDefinitions, string selfType)
        {
            return allDefinitions?.FirstOrDefault(d => d.Type == selfType);
        }

        private static IList<Definition> GetPolymorphicDefinitions(IList<Definition> allDefinitions, string baseType)
        {
            var result = new List<Definition>();
            var hash = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(baseType);

            while(stack.Any())
            {
                var newBaseType = stack.Pop();
                var derivedDefinitions = allDefinitions?.Where(d => !string.IsNullOrEmpty(d.DiscriminatorValue) && d.AllOfTypes != null && d.AllOfTypes.Any(t => t == newBaseType)).ToList();
                
                derivedDefinitions.ForEach(d =>
                {
                    var derivedType = d.Type;

                    if (hash.Add(derivedType))
                    {
                        result.Add(d);
                        stack.Push(d.Type);
                    }
                });
                
            }

            return result;
        }

        private static IList<ParameterEntity> GetDefinitionParameters(IList<Definition> allDefinitions, Definition definition, bool filterReadOnly = true)
        {
            var parameters = new List<ParameterEntity>();

            foreach (var property in definition.DefinitionProperties)
            {
                if (!filterReadOnly || (filterReadOnly == true && property.IsReadOnly == false))
                {
                    string typesTitle = null;
                    var types = new List<BaseParameterTypeEntity>();

                    var parameterTypeEntity = new BaseParameterTypeEntity
                    {
                        Id = property.Type,
                    };

                    if (property.Name == property.DiscriminatorKey && !string.IsNullOrEmpty(property.DiscriminatorValue))
                    {
                        typesTitle = "string";
                        types.Add(new BaseParameterTypeEntity
                        {
                            Id = property.DiscriminatorValue
                        });
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(property.AdditionalType))
                        {
                            parameterTypeEntity.IsDictionary = true;
                            parameterTypeEntity.Id = "object";
                            parameterTypeEntity.AdditionalTypes = new List<IdentifiableEntity>
                            {
                                new IdentifiableEntity{ Id = "string" },
                                new IdentifiableEntity{ Id = property.AdditionalType }
                            };
                            types.Add(parameterTypeEntity);
                        }
                        else if (property.DefinitionObjectType == DefinitionObjectType.Enum && string.IsNullOrEmpty(property.Type))
                        {
                            typesTitle = "enum";
                            foreach (var enumValue in property.EnumValues)
                            {
                                types.Add(new BaseParameterTypeEntity
                                {
                                    Id = enumValue.Value
                                });
                            }
                        }
                        else if (property.DefinitionObjectType != DefinitionObjectType.Simple)
                        {
                            var polymorphicDefinitions = GetPolymorphicDefinitions(allDefinitions, property.Type);
                            if (polymorphicDefinitions?.Count > 0)
                            {
                                typesTitle = property.Type + (property.DefinitionObjectType == DefinitionObjectType.Array ? "[]" : string.Empty);
                                foreach (var polymorphicDefinition in polymorphicDefinitions)
                                {
                                    types.Add(new BaseParameterTypeEntity
                                    {
                                        Id = polymorphicDefinition.Type,
                                        IsArray = property.DefinitionObjectType == DefinitionObjectType.Array
                                    });
                                }
                            }
                            else
                            {
                                types.Add(new BaseParameterTypeEntity
                                {
                                    Id = property.Type,
                                    IsArray = property.DefinitionObjectType == DefinitionObjectType.Array
                                });
                            }
                        }
                        else
                        {
                            types.Add(parameterTypeEntity);
                        }
                    }

                    parameters.Add(new ParameterEntity
                    {
                        Name = property.Name,
                        Description = Utility.GetDefinitionPropertyDescription(property),
                        IsRequired = property.IsRequired,
                        IsReadOnly = property.IsReadOnly,
                        Types = types,
                        TypesTitle = string.IsNullOrEmpty(typesTitle) ? null : typesTitle,
                        In = "body",
                        ParameterEntityType = ParameterEntityType.Body,
                        Pattern = property.Pattern,
                        Format = property.Format
                    });
                }
            }

            return parameters;
        }

        private static IList<DefinitionObject> FlattenDefinitionObject(DefinitionObject definitionObject)
        {
            var definitionObjects = new List<DefinitionObject>() { definitionObject };
            foreach (var item in definitionObject.PropertyItems)
            {
                if (definitionObjects.Any(d => d.Type == item.Type))
                {
                    continue;
                }
                if (item.DefinitionObjectType != DefinitionObjectType.Simple)
                {
                    definitionObjects.Add(item);

                    var tmpDefinitions = FlattenDefinitionObject(item);
                    foreach (var tmpDefinition in tmpDefinitions)
                    {
                        if (!definitionObjects.Any(d => d.Type == tmpDefinition.Type))
                        {
                            definitionObjects.Add(tmpDefinition);
                        }
                    }
                }
            }
            foreach (var allOf in definitionObject.AllOfs)
            {
                var tmpDefinitions = FlattenDefinitionObject(allOf);
                foreach (var tmpDefinition in tmpDefinitions)
                {
                    if (!definitionObjects.Any(d => d.Type == tmpDefinition.Type))
                    {
                        definitionObjects.Add(tmpDefinition);
                    }
                }
            }
            return definitionObjects;
        }

        private static IList<Definition> ResolveAllDefinitions(IList<Definition> definitions, DefinitionObject bodyDefinitionObject, IList<DefinitionObject> responseDefinitionObjects)
        {
            if (definitions == null)
            {
                Console.WriteLine("null herer");
            }
            var allDefinitions = new List<Definition>(definitions);
            var definitionObjects = new List<DefinitionObject>();
            responseDefinitionObjects.Add(bodyDefinitionObject);

            foreach (var responseDefinitionObject in responseDefinitionObjects)
            {
                var flattenDefinitionObjects = FlattenDefinitionObject(responseDefinitionObject);
                foreach (var flattenDefinitionObject in flattenDefinitionObjects)
                {
                    if (!string.IsNullOrEmpty(flattenDefinitionObject.Type)
                    && string.IsNullOrEmpty(flattenDefinitionObject.DiscriminatorKey)
                    && (flattenDefinitionObject.DefinitionObjectType == DefinitionObjectType.Object
                    || flattenDefinitionObject.DefinitionObjectType == DefinitionObjectType.Enum
                    || (flattenDefinitionObject.DefinitionObjectType == DefinitionObjectType.Array && flattenDefinitionObject.PropertyItems?.Count > 0)
                    || (flattenDefinitionObject.DefinitionObjectType == DefinitionObjectType.Array && flattenDefinitionObject.AllOfs?.Count > 0))
                    && !definitionObjects.Any(d => d.Type == flattenDefinitionObject.Type))
                    {
                        definitionObjects.Add(flattenDefinitionObject);
                    }
                }
            }

            var resolvedDefinitions = GetAllDefinitions(definitionObjects);
            foreach (var resolvedDefinition in resolvedDefinitions)
            {
                if (!definitions.Any(d => d.Type == resolvedDefinition.Type))
                {
                    allDefinitions.Add(resolvedDefinition);
                }
            }
            return allDefinitions;
        }

        private static IList<DefinitionEntity> TransformDefinitions(IList<Definition> allDefinitions, List<Definition> parametersDefinitions, DefinitionObject bodyDefinitionObject, IList<DefinitionObject> responseDefinitionObjects, out Dictionary<string, string> typeToName)
        {
            var resolvedAllDefinitions = ResolveAllDefinitions(allDefinitions, bodyDefinitionObject, responseDefinitionObjects);
            var definitions = new List<DefinitionEntity>();
            var typesDictionary = new Dictionary<string, bool>();
            var typesQueue = new Queue<string>();
            if (!string.IsNullOrEmpty(bodyDefinitionObject.Type))
            {
                var polymorphicDefinitions = GetPolymorphicDefinitions(resolvedAllDefinitions, bodyDefinitionObject.Type);
                if (polymorphicDefinitions?.Count > 0)
                {
                    foreach (var polymorphicDefinition in polymorphicDefinitions)
                    {
                        if (!string.IsNullOrEmpty(polymorphicDefinition.Type))
                        {
                            typesQueue.Enqueue(polymorphicDefinition.Type);
                        }
                    }
                }
            }
            var bodyProperties = GetDefinitionProperties(bodyDefinitionObject);
            foreach(var parametersDefinition in parametersDefinitions)
            {
                resolvedAllDefinitions.Add(parametersDefinition);
                typesQueue.Enqueue(parametersDefinition.Type);
            }
            foreach (var bodyProperty in bodyProperties)
            {
                if ((!string.IsNullOrEmpty(bodyProperty.Type) || !string.IsNullOrEmpty(bodyProperty.AdditionalType))
                    && (bodyProperty.DefinitionObjectType == DefinitionObjectType.Object || bodyProperty.DefinitionObjectType == DefinitionObjectType.Array)
                    && !typesQueue.Any(t => t == bodyProperty.Type))
                {
                    if (!string.IsNullOrEmpty(bodyProperty.AdditionalType))
                    {
                        typesQueue.Enqueue(bodyProperty.AdditionalType);
                    }
                    else
                    {
                        typesQueue.Enqueue(bodyProperty.Type);
                    }
                }
            }
            foreach (var responseDefinitionObject in responseDefinitionObjects)
            {
                if ((!string.IsNullOrEmpty(responseDefinitionObject.Type) || !string.IsNullOrEmpty(responseDefinitionObject.AdditionalType))
                    && (responseDefinitionObject.DefinitionObjectType == DefinitionObjectType.Object || responseDefinitionObject.DefinitionObjectType == DefinitionObjectType.Array)
                    && !typesQueue.Any(t => t == responseDefinitionObject.Type))
                {
                    if (!string.IsNullOrEmpty(responseDefinitionObject.AdditionalType))
                    {
                        typesQueue.Enqueue(responseDefinitionObject.AdditionalType);
                    }
                    else
                    {
                        typesQueue.Enqueue(responseDefinitionObject.Type);
                    }
                }
            }

            while (typesQueue.Count > 0)
            {
                var type = typesQueue.Dequeue();
                if (typesDictionary.TryGetValue(type, out var typeValue))
                {
                    continue;
                }
                typesDictionary[type] = true;

                var polymorphicDefinitions = GetPolymorphicDefinitions(resolvedAllDefinitions, type);
                if (polymorphicDefinitions?.Count > 0)
                {
                    foreach (var polymorphicDefinition in polymorphicDefinitions)
                    {
                        if (!string.IsNullOrEmpty(polymorphicDefinition.Type))
                        {
                            typesQueue.Enqueue(polymorphicDefinition.Type);
                        }
                    }
                }

                var selfDefinition = GetSelfDefinition(resolvedAllDefinitions, type);
                if (selfDefinition != null)
                {
                    if (selfDefinition.DefinitionObjectType == DefinitionObjectType.Enum)
                    {
                        definitions.Add(new DefinitionEntity
                        {
                            Name = selfDefinition.Name,
                            Type = selfDefinition.Type,
                            ShortType = selfDefinition.ShortType,
                            Description = Utility.GetDefinitionDescription(selfDefinition),
                            Kind = "enum",
                            ParameterItems = selfDefinition.EnumValues.Select(p => new DefinitionParameterEntity
                            {
                                Name = p.Value,
                                Types = new List<BaseParameterTypeEntity>
                                {
                                    new BaseParameterTypeEntity
                                    {
                                        Id = "string"
                                    }
                                },
                                Description = p.Description
                            }).ToList()
                        });
                    }
                    else if (selfDefinition.DefinitionObjectType != DefinitionObjectType.Simple)
                    {
                        var parameters = GetDefinitionParameters(resolvedAllDefinitions, selfDefinition, false).ToList();
                        definitions.Add(new DefinitionEntity
                        {
                            Name = selfDefinition.Name,
                            Type = selfDefinition.Type,
                            ShortType = selfDefinition.ShortType,
                            Description = Utility.GetDefinitionDescription(selfDefinition),
                            Kind = "object",
                            ParameterItems = parameters?.Select(p => new DefinitionParameterEntity
                            {
                                Id = p.Id,
                                Name = p.Name,
                                Description = p.Description,
                                IsReadOnly = p.IsReadOnly,
                                Types = p.Types,
                                TypesTitle = p.TypesTitle,
                                Pattern = p.Pattern,
                                Format = p.Format
                            }).ToList()
                        });

                        foreach (var definitionProperty in selfDefinition.DefinitionProperties)
                        {
                            if (!string.IsNullOrEmpty(definitionProperty.AdditionalType))
                            {
                                typesQueue.Enqueue(definitionProperty.AdditionalType);
                            }
                            else if (!string.IsNullOrEmpty(definitionProperty.Type))
                            {
                                typesQueue.Enqueue(definitionProperty.Type);
                            }
                        }
                    }
                }        
            }

            var result = new List<DefinitionEntity>();
            var nameToDefinition = new Dictionary<string, DefinitionEntity>();
            typeToName = new Dictionary<string, string>();

            foreach(var group in definitions.GroupBy(d => d.Name))
            {
                var tempItems = group.OrderBy(d => string.IsNullOrEmpty(d.Type) ? 0 : d.Type.Length).ToList();
                foreach (var definition in tempItems)
                {
                    if (!nameToDefinition.ContainsKey(definition.Name))
                    {
                        result.Add(definition);
                        nameToDefinition[definition.Name] = definition;
                    }
                    else if (!definition.Type.EndsWith(nameToDefinition[definition.Name].Type) || string.IsNullOrEmpty(nameToDefinition[definition.Name].ShortType))
                    {
                        definition.Name = definition.Type;
                        result.Add(definition);
                        nameToDefinition[definition.Name] = definition;
                    }

                    typeToName[definition.Type] = definition.Name;
                }
            }

            return result;
        }

        #endregion

        #region Security

        private static IList<SecurityEntity> GetAllSecurityEntities(SwaggerModel swaggerModel)
        {
            var allSecurities = new List<SecurityEntity>();
            var securityDefinitionsModel = swaggerModel.Metadata.GetDictionaryFromMetaData<Dictionary<string, JObject>>("securityDefinitions");
            if (securityDefinitionsModel != null)
            {
                foreach (var definition in securityDefinitionsModel)
                {
                    var securityEntity = new SecurityEntity
                    {
                        Key = definition.Key
                    };
                    var definitionValue = definition.Value.ToObject<Dictionary<string, object>>();
                    if(definitionValue != null)
                    {
                        securityEntity.Name = definitionValue.GetValueFromMetaData<string>("name");
                        securityEntity.Type = definitionValue.GetValueFromMetaData<string>("type");
                        securityEntity.Description = definitionValue.GetValueFromMetaData<string>("description");
                        securityEntity.In = definitionValue.GetValueFromMetaData<string>("in");
                        securityEntity.Flow = definitionValue.GetValueFromMetaData<string>("flow");
                        securityEntity.AuthorizationUrl = definitionValue.GetValueFromMetaData<string>("authorizationUrl");
                        securityEntity.TokenUrl = definitionValue.GetValueFromMetaData<string>("tokenUrl");
                        var scopes = definitionValue.GetDictionaryFromMetaData<Dictionary<string, string>>("scopes");
                        if (scopes != null)
                        {
                            securityEntity.Scopes = new List<SecurityScopeEntity>();
                            foreach(var scope in scopes)
                            {
                                securityEntity.Scopes.Add(new SecurityScopeEntity
                                {
                                    Name = scope.Key,
                                    Description = scope.Value
                                });
                            }
                        }
                    }
                    allSecurities.Add(securityEntity);
                }
            }
            return allSecurities;
        }

        private static IList<SecurityEntity> TransformSecurities(RestApiChildItemViewModel viewModel, SwaggerModel swaggerModel)
        {
            var securities = new List<SecurityEntity>();

            var securitiesModel = viewModel.Metadata.GetArrayFromMetaData<JObject>("security");
            if(securitiesModel == null)
            {
                securitiesModel = swaggerModel.Metadata.GetArrayFromMetaData<JObject>("security");
            }
            if (securitiesModel != null)
            {
                var allSecurities = GetAllSecurityEntities(swaggerModel);
                foreach (var securityModel in securitiesModel)
                {
                    var security = securityModel.ToObject<Dictionary<string, object>>().FirstOrDefault();
                    var foundSecurity = allSecurities.FirstOrDefault(s => s.Key == security.Key);
                    var scopes = security.Value ==null ? null :((JArray)security.Value).ToObject<string[]>();
                    var securityEntity = new SecurityEntity
                    {
                        Name = foundSecurity?.Name ?? security.Key,
                        Type = foundSecurity?.Type,
                        Description = foundSecurity?.Description,
                        In = foundSecurity?.In,
                        Flow = foundSecurity?.Flow,
                        AuthorizationUrl = foundSecurity?.AuthorizationUrl,
                        TokenUrl = foundSecurity?.TokenUrl,
                        Scopes = scopes == null ? null : foundSecurity.Scopes?.Where(s => scopes.Any(sc => s.Name == sc)).ToList()
                    };
                    securities.Add(securityEntity);
                }
            }

            return securities;
        }

        #endregion

        #region MetaData
        private static MetaDataEntity TransformMetaData(string sourceUrl,string summary,string serviceName)
        {
            return new MetaDataEntity
            {
                SourceUrl = sourceUrl,
                Description = Utility.ExtractMetaDataDescription(summary, serviceName)
            };
        }

        #endregion

        #region ErrorCodes

        private static IList<ErrorCodeEntity> TransformErrorCodes(RestApiChildItemViewModel viewModel, SwaggerModel swaggerModel)
        {
            var errorCodes = new List<ErrorCodeEntity>();

            var errorCodeNames = viewModel.Metadata.GetArrayFromMetaData<string>("x-ms-docs-errors");
            if (errorCodeNames != null && errorCodeNames.Count() > 0)
            {
                var allErrorCodes = swaggerModel.Metadata.GetDictionaryFromMetaData<Dictionary<string, JObject>>("x-ms-docs-errors-mapping");
                if (allErrorCodes != null && allErrorCodes.Count > 0)
                {
                    foreach (var errorCodeName in errorCodeNames)
                    {
                        if (allErrorCodes.TryGetValue(errorCodeName, out var errorCode))
                        {
                            var errorCodeEntity = new ErrorCodeEntity
                            {
                                Name = (string)errorCode.GetValue("name"),
                                Code = (string)errorCode.GetValue("id"),
                            };
                            errorCodes.Add(errorCodeEntity);
                        }
                    }
                }
            }

            return errorCodes;
        }

        #endregion

        #region Parse JObject to DefinitionObject

        private static void ResolveObject(string key, JObject nodeObject, DefinitionObject definitionObject, string[] requiredFields = null, string discriminatorKey = null, string discriminatorValue = null, string parentType = "")
        {
            if (nodeObject.Type == JTokenType.Object)
            {
                var nodeObjectDict = nodeObject.ToObject<Dictionary<string, object>>();
                var refName = nodeObjectDict.GetValueFromMetaData<string>("x-internal-ref-name");
                if (string.IsNullOrEmpty(refName))
                {
                    refName = nodeObjectDict.GetValueFromMetaData<string>("x-internal-loop-ref-name");
                }
                var currentType = nodeObjectDict.GetValueFromMetaData<string>("type");
                definitionObject.Name = key ?? refName;
                definitionObject.Type = refName;
                definitionObject.Description = nodeObjectDict.GetValueFromMetaData<string>("description");
                definitionObject.Title = nodeObjectDict.GetValueFromMetaData<string>("title");
               
                definitionObject.IsReadOnly = nodeObjectDict.GetValueFromMetaData<bool>("readOnly");
                definitionObject.IsFlatten = definitionObject.IsFlatten ? true : nodeObjectDict.GetValueFromMetaData<bool>("x-ms-client-flatten");

                if (requiredFields != null && requiredFields.Any(v => v == definitionObject.Name))
                {
                    definitionObject.IsRequired = true;
                }
                var requiredProperties = nodeObjectDict.GetArrayFromMetaData<string>("required");

                if (requiredFields != null)
                {
                    if (requiredProperties == null)
                    {
                        requiredProperties = requiredFields;
                    }
                    else
                    {
                        var requiredPropertiesList = requiredProperties.ToList();
                        requiredPropertiesList.AddRange(requiredFields.ToList());
                        requiredProperties = requiredPropertiesList.ToArray();
                    }
                }

                definitionObject.DiscriminatorKey = nodeObjectDict.GetValueFromMetaData<string>("discriminator");
                definitionObject.DiscriminatorValue = nodeObjectDict.GetValueFromMetaData<string>("x-ms-discriminator-value");
                var discriminatorPropertyKey = definitionObject.DiscriminatorKey;
                var discriminatorPropertyValue = string.IsNullOrEmpty(discriminatorValue) ? definitionObject.DiscriminatorValue : discriminatorValue;

                var allOf = nodeObjectDict.GetArrayFromMetaData<JObject>("allOf");
                if (allOf != null && allOf.Count() > 0)
                {
                    definitionObject.DefinitionObjectType = DefinitionObjectType.Object;
                    foreach (var oneAllOf in allOf)
                    {
                        var childDefinitionObject = new DefinitionObject();
                        childDefinitionObject.IsFlatten = definitionObject.IsFlatten;
                        ResolveObject(string.Empty, oneAllOf, childDefinitionObject, requiredProperties, discriminatorPropertyKey, discriminatorPropertyValue);
                        definitionObject.AllOfs.Add(childDefinitionObject);
                    }
                }

                if (nodeObjectDict.GetValueFromMetaData<JObject>("properties") != null)
                {
                    definitionObject.DefinitionObjectType = DefinitionObjectType.Object;
                    var propertiesNode = nodeObjectDict.GetValueFromMetaData<JObject>("properties"); 

                    var properties = propertiesNode.ToObject<Dictionary<string, object>>();
                    foreach (var property in properties)
                    {
                        var childDefinitionObject = new DefinitionObject();
                        var tempParentType = definitionObject.Type ?? definitionObject.Name;
                        if(!string.IsNullOrEmpty(parentType))
                        {
                            tempParentType = parentType + "." + tempParentType;
                        }
                        ResolveObject(property.Key, (JObject)property.Value, childDefinitionObject, requiredProperties, discriminatorPropertyKey, discriminatorPropertyValue, tempParentType);
                        definitionObject.PropertyItems.Add(childDefinitionObject);
                    }
                }
                else if (nodeObjectDict.GetValueFromMetaData<JObject>("additionalProperties") != null)
                {
                    definitionObject.DefinitionObjectType = DefinitionObjectType.Object;
                    var additionalPropertiesNode = nodeObjectDict.GetValueFromMetaData<JObject>("additionalProperties");
                    var additionalProperties = additionalPropertiesNode.ToObject<Dictionary<string, object>>();
                    var additionalType = additionalProperties.GetValueFromMetaData<string>("type");
                    var additionalPropertyProperties = additionalProperties.GetDictionaryFromMetaData<Dictionary<string, object>>("properties");

                    if (additionalPropertyProperties != null)
                    {
                        var childDefinitionObject = new DefinitionObject();
                        definitionObject.AdditionalType = additionalProperties.GetValueFromMetaData<string>("x-internal-ref-name");
                        ResolveObject(string.Empty, additionalPropertiesNode, childDefinitionObject, requiredProperties, discriminatorPropertyKey, discriminatorPropertyValue);
                        definitionObject.PropertyItems.Add(childDefinitionObject);
                    }
                    else
                    {
                        definitionObject.DefinitionObjectType = DefinitionObjectType.Simple;
                        definitionObject.AdditionalType = additionalType;
                    }
                }
                else if (nodeObjectDict.GetValueFromMetaData<JObject>("items") != null)
                {
                    definitionObject.DefinitionObjectType = DefinitionObjectType.Array;
                    var itemsDefine = nodeObjectDict.GetValueFromMetaData<JObject>("items");
                    if (itemsDefine.TryGetValue("x-internal-ref-name", out var type))
                    {
                        definitionObject.Type = (string)type;
                    }
                    else if (itemsDefine.TryGetValue("x-internal-loop-ref-name", out var loopType))
                    {
                        definitionObject.Type = (string)loopType;
                    }

                    if (itemsDefine.TryGetValue("description", out var subDescription))
                    {
                        definitionObject.SubDescription = (string)subDescription;
                    }
                    if (itemsDefine.TryGetValue("title", out var title))
                    {
                        definitionObject.SubTitle = (string)title;
                    }

                    if (itemsDefine.TryGetValue("x-ms-discriminator-value", out var innerDiscriminatorValue))
                    {
                        definitionObject.DiscriminatorValue = (string)innerDiscriminatorValue;
                        discriminatorPropertyValue = (string)innerDiscriminatorValue;
                    }
                    if (itemsDefine.TryGetValue("discriminator", out var innerDiscriminatorKey))
                    {
                        definitionObject.DiscriminatorKey = (string)innerDiscriminatorKey;
                        discriminatorPropertyKey = (string)innerDiscriminatorKey;
                    }

                    if (itemsDefine.TryGetValue("allOf", out var allOfsNode))
                    {
                        foreach (var oneAllOf in allOfsNode.ToObject<JArray>())
                        {
                            var childDefinitionObject = new DefinitionObject();
                            ResolveObject(string.Empty, (JObject)oneAllOf, childDefinitionObject, requiredProperties, discriminatorPropertyKey, discriminatorPropertyValue);
                            definitionObject.AllOfs.Add(childDefinitionObject);
                        }
                    }
                    if (itemsDefine.TryGetValue("properties", out var propertiesNode))
                    {
                        var properties = propertiesNode.ToObject<Dictionary<string, object>>();
                        foreach (var property in properties)
                        {
                            var childDefinitionObject = new DefinitionObject();

                            ResolveObject(property.Key, (JObject)property.Value, childDefinitionObject, requiredProperties, discriminatorPropertyKey, discriminatorPropertyValue);
                            definitionObject.PropertyItems.Add(childDefinitionObject);
                        }
                    }
                    else if (itemsDefine.TryGetValue("type", out var itemType))
                    {
                        if (!(allOfsNode != null && itemType.ToString().ToLowerInvariant() == "object"))
                        {
                            definitionObject.Type = (string)itemType;
                        }
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(discriminatorKey) && string.Equals(discriminatorKey, definitionObject.Name))
                    {
                        definitionObject.DiscriminatorKey = discriminatorKey;
                        definitionObject.DiscriminatorValue = discriminatorValue;
                    }

                    var enumValues = nodeObjectDict.GetArrayFromMetaData<string>("enum");
                    if (enumValues != null)
                    {
                        definitionObject.DefinitionObjectType = DefinitionObjectType.Enum;
                        var enumObjects = new List<EnumValue>();
                        foreach (var v in enumValues)
                        {
                            enumObjects.Add(new EnumValue { Value = v });
                        }

                        var enumNode = nodeObjectDict.GetDictionaryFromMetaData<Dictionary<string, object>>("x-ms-enum");
                        if (enumNode != null && enumNode.TryGetValue("name", out var enumName))
                        {
                            definitionObject.Type = (string)enumName;
                        }
                        if (enumNode != null && enumNode.TryGetValue("values", out var enumValue))
                        {
                            var values = (JArray)enumValue;
                            if (values != null)
                            {
                                foreach (var v in values)
                                {
                                    var keyValueEnum = v.ToObject<Dictionary<string, object>>();
                                    var enumV = keyValueEnum.GetValueFromMetaData<string>("value");
                                    var enumObject = enumObjects.FirstOrDefault(q => q.Value == enumV);
                                    if (enumV != null && enumObject != null)
                                    {
                                        enumObject.Description = keyValueEnum.GetValueFromMetaData<string>("description");
                                        var name = keyValueEnum.GetValueFromMetaData<string>("name");
                                        enumObject.Name = string.IsNullOrEmpty(name) ? enumV : name;
                                    }
                                }
                            }
                        }

                        definitionObject.EnumValues = enumObjects;
                    }
                    else if (nodeObjectDict.TryGetValue("x-internal-loop-ref-name", out var loopName))
                    {
                        definitionObject.DefinitionObjectType = DefinitionObjectType.Simple;
                        definitionObject.Type = (string)loopName;
                        var token = nodeObjectDict.GetDictionaryFromMetaData<Dictionary<string, object>>("x-internal-loop-token");
                        if (token != null)
                        {
                            definitionObject.Description = token.GetValueFromMetaData<string>("description");
                            definitionObject.IsReadOnly = token.GetValueFromMetaData<bool>("readOnly");
                        }
                    }
                    else if (definitionObject.AllOfs.Count == 0 && definitionObject.PropertyItems.Count == 0)
                    {
                        definitionObject.DefinitionObjectType = DefinitionObjectType.Simple;
                        definitionObject.Type = currentType;
                    }
                }

                if (definitionObject.DefinitionObjectType == DefinitionObjectType.Object || definitionObject.DefinitionObjectType == DefinitionObjectType.Array)
                {
                    if (string.IsNullOrEmpty(definitionObject.Type))
                    {
                        var name = definitionObject.Name.FirstLetterToUpper();
                        definitionObject.Type = string.IsNullOrEmpty(parentType)? name : parentType + "." + name;
                        if (!string.IsNullOrEmpty(name))
                        {
                            definitionObject.ShortType = name;
                        }
                    }
                }
            }
        }

        private static void ResolveDefinitionClientFlatten(DefinitionObject definitionObject, DefinitionObject parentDefinitionObject = null)
        {
            foreach (var allOf in definitionObject.AllOfs)
            {
                ResolveDefinitionClientFlatten(allOf, definitionObject);
            }

            foreach (var propertyItem in definitionObject.PropertyItems)
            {
                ResolveDefinitionClientFlatten(propertyItem, definitionObject);
                if (propertyItem.IsFlatten)
                {
                    var items = new List<DefinitionObject>();
                    foreach (var item in definitionObject.PropertyItems)
                    {
                        if (!item.IsFlatten || (item.DefinitionObjectType == DefinitionObjectType.Simple && !string.IsNullOrEmpty(item.AdditionalType)))
                        {
                            items.Add(item);
                        }
                    }
                    foreach (var item in propertyItem.PropertyItems)
                    {
                        // todo: should flatten the property as "properties.property"
                        item.Name = propertyItem.Name + "." + item.Name;
                        items.Add(item);
                    }
                    foreach (var allOf in propertyItem.AllOfs)
                    {
                        foreach(var item in allOf.PropertyItems)
                        {
                            item.Name = propertyItem.Name + "." + item.Name;
                        }
                        definitionObject.AllOfs.Add(allOf);
                    }
                     definitionObject.PropertyItems = items;
                    propertyItem.PropertyItems = new List<DefinitionObject>();
                }
            }
        }

        private static DefinitionObject ResolveSchema(JObject nodeObject)
        {
            DefinitionObject definitionObject = new DefinitionObject();
            ResolveObject(string.Empty, nodeObject, definitionObject);
            ResolveDefinitionClientFlatten(definitionObject);
            return definitionObject;
        }

        #endregion
    }
}
