namespace dnSpy.MCP.Mcp {
    /// <summary>
    /// MCP server configuration options
    /// </summary>
    public sealed class McpServerOptions {
        /// <summary>
        /// TCP port to listen on. Default is 5150.
        /// </summary>
        public int Port { get; set; } = 5150;

        /// <summary>
        /// Hostname to bind to. Default is localhost (127.0.0.1).
        /// </summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>
        /// Whether the server is currently running.
        /// </summary>
        public bool IsRunning { get; set; }
    }
}
