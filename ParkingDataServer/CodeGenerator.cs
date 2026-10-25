using System.Collections.Generic;
using System.Text;

namespace System.Migration
{
    public class CodeGenerator
    {
        public class Line
        {
            private readonly List<string> _lines = new List<string>();
            private int _indent = 0;

            public Line Add(string text)
            {
                _lines.Add(new string('\t', _indent) + text);
                return this;
            }

            public Line Tab(int indent)
            {
                _indent = indent;
                return this;
            }

            public override string ToString()
            {
                return string.Join(" ", _lines);
            }
        }
    }
}