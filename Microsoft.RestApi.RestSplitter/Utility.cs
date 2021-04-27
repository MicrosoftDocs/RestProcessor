﻿namespace Microsoft.RestApi.RestSplitter
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using Microsoft.RestApi.Common;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public static class Utility
    {
        private static readonly JsonSerializer JsonSerializer = new JsonSerializer
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        };

        public static bool ShouldSplitToOperation(JObject root, int splitOperationCountGreaterThan)
        {
            var paths = ((JObject)root["paths"]);
            return paths.Count > splitOperationCountGreaterThan || (paths.Count == splitOperationCountGreaterThan && paths.Values().First().Values().Count() > splitOperationCountGreaterThan);
        }

        public static readonly Regex YamlHeaderRegex = new Regex(@"^\-{3}(?:\s*?)\n([\s\S]+?)(?:\s*?)\n\-{3}(?:\s*?)(?:\n|$)", RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(10));
        public static readonly YamlDotNet.Serialization.Deserializer YamlDeserializer = new YamlDotNet.Serialization.Deserializer();
        public static readonly YamlDotNet.Serialization.Serializer YamlSerializer = new YamlDotNet.Serialization.Serializer();
        public static readonly string Pattern = @"(?:{0}|[A-Z]+?(?={0}|[A-Z][a-z]|$)|[A-Z](?:[0-9]*?)(?:[a-z]*?)(?={0}|[A-Z]|$)|(?:[a-z]+?)(?={0}|[A-Z]|$))";
        public static readonly HashSet<string> Keyword = new HashSet<string> {
            "BI", "IP", "ML", "MAM", "OS", "VMs", "VM", "APIM", "vCenters", "WANs", "WAN", "IDs", "ID", "REST", "OAuth2", "SignalR", "iOS", "IOS",
            "PlayFab", "OpenId", "NuGet"
        };

        public static Tuple<string, string> Serialize(string targetDir, string name, JObject root)
        {
            var fileName = $"{name}.json";
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }
            var filePath = Path.Combine(targetDir, fileName);
            if (File.Exists(filePath))
            {
                throw new Exception($"There alreay exist a file: {filePath}");
            }
            using (var sw = new StreamWriter(filePath))
            using (var writer = new JsonTextWriter(sw))
            {
                JsonSerializer.Serialize(writer, root);
            }
            return new Tuple<string, string>(fileName, Path.Combine(targetDir, fileName));
        }

        public static void Serialize(TextWriter writer, object obj)
        {
            JsonSerializer.Serialize(writer, obj);
        }

        public static object GetYamlHeaderByMeta(string filePath, string metaName)
        {
            var yamlHeader = GetYamlHeader(filePath);
            object result;
            if (yamlHeader != null && yamlHeader.TryGetValue(metaName, out result))
            {
                return result;
            }
            return null;
        }

        public static Dictionary<string, object> GetYamlHeader(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File path {filePath} not exists when parsing yaml header.");
            }

            var markdown = File.ReadAllText(filePath);

            var match = YamlHeaderRegex.Match(markdown);
            if (match.Length == 0)
            {
                return null;
            }

            // ---
            // a: b
            // ---
            var value = match.Groups[1].Value;
            try
            {
                using (StringReader reader = new StringReader(value))
                {
                    return YamlDeserializer.Deserialize<Dictionary<string, object>>(reader);
                }
            }
            catch (Exception)
            {
                Console.WriteLine();
                return null;
            }
        }

        public static T YamlDeserialize<T>(TextReader stream)
        {
            return YamlDeserializer.Deserialize<T>(stream);
        }

        public static void Serialize(string path, object obj)
        {
            using (var stream = File.Create(path))
            using (var writer = new StreamWriter(stream))
            {
                JsonSerializer.Serialize(writer, obj);
            }
        }

        public static T ReadFromFile<T>(string mappingFilePath)
        {
            using (var streamReader = File.OpenText(mappingFilePath))
            using (var reader = new JsonTextReader(streamReader))
            {
                return JsonSerializer.Deserialize<T>(reader);
            }
        }

        public static string ExtractPascalNameByRegex(string name, List<string>noSplitWords)
        {
            if (name.Contains(" "))
            {
                return name;
            }
            if (name.Contains("_") || name.Contains("-"))
            {
                return name.Replace('_', ' ').Replace('-', ' ');
            }

            var result = new List<string>();


            var p = string.Format(Pattern, string.Join("|", noSplitWords?.Count > 0 ? Keyword.Concat(noSplitWords).Distinct() : Keyword));
            while (name.Length > 0)
            {
                var m = Regex.Match(name, p);
                if (!m.Success)
                {
                    return name;
                }
                result.Add(m.Value);
                name = name.Substring(m.Length);
            }
            return string.Join(" ", result);
        }

        public static string ExtractPascalFileNameByRegex(string name, List<string> noSplitWords, string splitChar)
        {
            var result = new List<string>();
            foreach (var child in name.Split(' ', '_'))
            {
                var p = string.Format(Pattern, string.Join("|", noSplitWords?.Count > 0 ? Keyword.Concat(noSplitWords).Distinct() : Keyword));
                var temp = child;
                while (temp.Length > 0)
                {
                    var m = Regex.Match(temp, p);
                    if (!m.Success)
                    {
                        return name;
                    }
                    result.Add(m.Value);
                    temp = temp.Substring(m.Length);
                }
            }

            return string.Join(splitChar, result);
        }

        public static string ExtractPascalName(string name)
        {
            if (name.Contains(" "))
            {
                return name;
            }

            var result = new StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                // Exclude index = 0
                var c = name[i];
                if (i != 0 &&
                    char.IsUpper(c))
                {
                    var closestUpperCaseWord = GetClosestUpperCaseWord(name, i);
                    if (closestUpperCaseWord.Length > 0)
                    {
                        if (Keyword.Contains(closestUpperCaseWord))
                        {
                            result.Append(" ");
                            result.Append(closestUpperCaseWord);
                            i = i + closestUpperCaseWord.Length - 1;
                            continue;
                        }

                        var closestCamelCaseWord = GetClosestCamelCaseWord(name, i);
                        if (Keyword.Contains(closestCamelCaseWord))
                        {
                            result.Append(" ");
                            result.Append(closestCamelCaseWord);
                            i = i + closestCamelCaseWord.Length - 1;
                            continue;
                        }
                    }
                    result.Append(" ");
                }
                result.Append(c);
            }

            return result.ToString();
        }

        public static string TrimSubGroupName(this string groupName)
        {
            if (string.IsNullOrEmpty(groupName))
            {
                return string.Empty;
            }
            return groupName.Replace(" ", "").Trim().ToLower();
        }

        public static string TryToFormalizeUrl(string path, bool isFormalized)
        {
            Guard.ArgumentNotNullOrEmpty(path, "FormalizedUrl");

            if (isFormalized)
            {
                return path
                    .Replace("%", "")
                    .Replace("\\", "")
                    .Replace("\"", "")
                    .Replace("^", "")
                    .Replace("`", "")
                    .Replace('<', '(')
                    .Replace('>', ')')
                    .Replace("{", "((")
                    .Replace("}", "))")
                    .Replace('|', '_')
                    .Replace(' ', '-');
            }
            else
            {
                return path;
            }
        }

        public static void WriteDictToFile(string filePath, IDictionary<string, string> dict)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    using (File.Create(filePath))
                    {
                    }
                }

                using (var sw = new StreamWriter(filePath, true))
                {
                    string json = JsonConvert.SerializeObject(dict, Formatting.Indented);
                    sw.WriteLine(json);
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Write dict file error: {ex}, filePath: {filePath}, DictionaryObj: {dict.ToString()}");
                return;
            }
        }

        public static void ClearFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.WriteAllText(filePath, string.Empty);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Clear file error: {ex}");
                return;
            }
        }

        public static string GetSourceSwaggerFilePath(string filePath)
        {
            ////1. According to the pattern which is a contract with powershell script
            var sourceSwaggerFileName = Path.GetFileName(filePath)?.Replace(".json", "_sourceswagger.json");
            var sourceSwaggerFilePath = Path.Combine(Path.GetDirectoryName(filePath), sourceSwaggerFileName);

            ////2. Judge the file is exist or using the filePath
            if (!File.Exists(sourceSwaggerFilePath))
            {
                return filePath;
            }
            else
            {
                return sourceSwaggerFilePath;
            }
        }

        private static string GetClosestUpperCaseWord(string word, int index)
        {
            var result = new StringBuilder();
            for (var i = index; i < word.Length; i++)
            {
                var character = word[i];
                if (char.IsUpper(character))
                {
                    result.Append(character);
                }
                else
                {
                    break;
                }
            }

            if (result.Length == 0)
            {
                return string.Empty;
            }

            // Remove the last character, which is unlikely the continues upper case word.
            return result.ToString(0, result.Length - 1);
        }

        private static string GetClosestCamelCaseWord(string word, int index)
        {
            var result = new StringBuilder();
            var meetLowerCase = false;
            for (var i = index; i < word.Length; i++)
            {
                var character = word[i];
                if (char.IsUpper(character))
                {
                    if (meetLowerCase)
                    {
                        return result.ToString();
                    }
                }
                else
                {
                    meetLowerCase = true;
                }
                result.Append(character);
            }

            return result.ToString();
        }
    }
}
