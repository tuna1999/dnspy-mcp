namespace dnSpy.MCP {
    /// <summary>
    /// Minimal contract exposed by the MCP extension entry point (<see cref="TheExtension"/>).
    /// Menu commands reach the running server via <see cref="TheExtension.Instance"/>,
    /// typed as this interface to avoid coupling menu items to the concrete host.
    /// </summary>
    internal interface IMcpExtension {
        /// <summary>Whether the MCP server is currently accepting requests.</summary>
        bool IsServerRunning { get; }

        /// <summary>TCP port the server is (or will) listen on.</summary>
        int ServerPort { get; }

        /// <summary>Start the server if it is not already running. Safe to call when running.</summary>
        void StartServer();

        /// <summary>Stop the running server, if any. Safe to call when stopped.</summary>
        void StopServer();

        /// <summary>
        /// Render a line to the "MCP Server" Output Pane (dnSpy UI). Rendering only —
        /// never writes to <c>McpLogger</c>: displaying existing logs must not create
        /// new log events (feedback loop).
        /// </summary>
        void WriteToOutputPane(string message);

        /// <summary>Clear the Output Pane (paired with the "Clear Log" menu item).</summary>
        void ClearOutputPane();
    }
}
