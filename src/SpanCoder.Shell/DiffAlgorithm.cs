using System;
using System.Collections.Generic;

namespace SpanCoder.Shell
{
    public enum DiffType
    {
        Unchanged,
        Added,
        Deleted
    }

    public class DiffLine
    {
        public DiffType Type { get; set; }
        public int? LeftLineNumber { get; set; }
        public string LeftText { get; set; } = "";
        public int? RightLineNumber { get; set; }
        public string RightText { get; set; } = "";
    }

    public static class DiffAlgorithm
    {
        public static List<DiffLine> ComputeDiff(string[] leftLines, string[] rightLines)
        {
            int m = leftLines.Length;
            int n = rightLines.Length;
            int[,] dp = new int[m + 1, n + 1];

            for (int i = 1; i <= m; i++)
            {
                for (int j = 1; j <= n; j++)
                {
                    if (leftLines[i - 1] == rightLines[j - 1])
                    {
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                    }
                    else
                    {
                        dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                    }
                }
            }

            var result = new List<DiffLine>();
            int x = m, y = n;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0 && leftLines[x - 1] == rightLines[y - 1])
                {
                    result.Add(new DiffLine
                    {
                        Type = DiffType.Unchanged,
                        LeftLineNumber = x,
                        LeftText = leftLines[x - 1],
                        RightLineNumber = y,
                        RightText = rightLines[y - 1]
                    });
                    x--;
                    y--;
                }
                else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
                {
                    result.Add(new DiffLine
                    {
                        Type = DiffType.Added,
                        LeftLineNumber = null,
                        LeftText = "",
                        RightLineNumber = y,
                        RightText = rightLines[y - 1]
                    });
                    y--;
                }
                else
                {
                    result.Add(new DiffLine
                    {
                        Type = DiffType.Deleted,
                        LeftLineNumber = x,
                        LeftText = leftLines[x - 1],
                        RightLineNumber = null,
                        RightText = ""
                    });
                    x--;
                }
            }

            result.Reverse();
            return result;
        }
    }
}
