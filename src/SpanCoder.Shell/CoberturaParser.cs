using System;
using System.Collections.Generic;

namespace SpanCoder.Shell
{
    public static class CoberturaParser
    {
        public static Dictionary<string, Dictionary<int, bool>> Parse(string xmlContent, string? workspaceRootPath = null)
        {
            var coverageData = new Dictionary<string, Dictionary<int, bool>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(xmlContent)) return coverageData;

            var doc = System.Xml.Linq.XDocument.Parse(xmlContent);
            foreach (var classEl in doc.Descendants("class"))
            {
                string filename = classEl.Attribute("filename")?.Value ?? "";
                if (string.IsNullOrEmpty(filename)) continue;

                string fullPath = filename;
                if (!System.IO.Path.IsPathRooted(fullPath) && !string.IsNullOrEmpty(workspaceRootPath))
                {
                    fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(workspaceRootPath, filename));
                }

                var lineCoverage = new Dictionary<int, bool>();
                foreach (var lineEl in classEl.Descendants("line"))
                {
                    int lineNum = int.Parse(lineEl.Attribute("number")?.Value ?? "0");
                    int hits = int.Parse(lineEl.Attribute("hits")?.Value ?? "0");
                    if (lineNum > 0)
                    {
                        lineCoverage[lineNum] = hits > 0;
                    }
                }

                coverageData[fullPath] = lineCoverage;
            }

            return coverageData;
        }

        public static Dictionary<string, Dictionary<int, bool>> ParseFile(string xmlPath, string? workspaceRootPath = null)
        {
            var coverageData = new Dictionary<string, Dictionary<int, bool>>(StringComparer.OrdinalIgnoreCase);
            if (!System.IO.File.Exists(xmlPath)) return coverageData;
            return Parse(System.IO.File.ReadAllText(xmlPath), workspaceRootPath);
        }
    }
}
