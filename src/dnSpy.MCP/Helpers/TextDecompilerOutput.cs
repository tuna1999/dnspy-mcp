using System;
using System.Text;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

namespace dnSpy.MCP.Helpers {
    /// <summary>
    /// Simple text output implementation for decompilation
    /// </summary>
    public sealed class TextDecompilerOutput : IDecompilerOutput {
        private readonly StringBuilder sb = new StringBuilder();
        private int length;

        public int Length => length;
        public int NextPosition => length;

        public override string ToString() => sb.ToString();

        public void IncreaseIndent() { }
        public void DecreaseIndent() { }
        public void WriteLine() {
            sb.AppendLine();
            length += 1;
        }

        public void Write(string text, object color) {
            if (string.IsNullOrEmpty(text)) return;
            sb.Append(text);
            length += text.Length;
        }

        public void Write(string text, int index, int length, object color) {
            if (string.IsNullOrEmpty(text) || length <= 0) return;
            sb.Append(text, index, length);
            this.length += length;
        }

        public void Write(string text, object? reference, DecompilerReferenceFlags flags, object color) {
            if (string.IsNullOrEmpty(text)) return;
            sb.Append(text);
            length += text.Length;
        }

        public void Write(string text, int index, int length, object? reference, DecompilerReferenceFlags flags, object color) {
            if (string.IsNullOrEmpty(text) || length <= 0) return;
            sb.Append(text, index, length);
            this.length += length;
        }

        public void AddCustomData<TData>(string id, TData data) { }
        public bool UsesCustomData => false;
    }
}
