using System;
using System.Text;

namespace HybridCLR.Editor.AssemblyShadow
{
    internal sealed class CanonicalSignatureWriter
    {
        private readonly StringBuilder builder = new StringBuilder();

        public void Token(string value)
        {
            if (value == null)
                value = string.Empty;
            builder.Append(value.Length).Append(':').Append(value).Append(';');
        }

        public void Line(string value)
        {
            Token(value);
            builder.Append('\n');
        }

        public override string ToString() { return builder.ToString(); }
    }
}
