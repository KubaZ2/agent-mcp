# Agent MCP Server

**Agent MCP Server** is a specialized Model Context Protocol (MCP) server that acts as a middleman orchestrator. Instead of merely exposing raw tools (like file system access or web search) to an MCP client, this server exposes a single, powerful `agent` tool.

By calling the `agent` tool, your primary MCP client can spawn and delegate complex, multi-step background tasks to specialized, configured AI agents (powered by OpenAI, Anthropic, or Ollama). These agents can dynamically connect to *other* downstream MCP servers, invoke their tools in parallel, wait for long-running tasks to finish, and ask for user permission before executing sensitive actions.

## 🌟 Key Features

* **Agent Delegation:** Exposes an `agent` tool that dynamically lists available configured agents, allowing a parent LLM to delegate work to specialized sub-agents.
* **Downstream MCP Federation:** Connects to other Stdio and HTTP MCP servers, passing their tools to your configured agents.
* **Multi-Provider Support:** Supports OpenAI, Anthropic, and Ollama compatible LLM backends.
* **Human-in-the-Loop (Elicitation):** Natively supports the MCP Elicitation capability. If an agent tries to call a tool, the server can pause and ask the user for approval (Approve Once, Approve Always, Deny Once, Deny Always). Also supports forwarding the elicitation prompts of downstream MCP servers to the client.
* **Granular Tool Filtering:** Configure default tool policies (`Ask` (default), `Allow`, `Deny`) and specify `AutoApproveTools` or `AutoDenyTools` using glob or regex patterns.
* **Long-Running Tasks:** Built-in support for the MCP Tasks extension. Agents can trigger asynchronous downstream tools, and the server will automatically poll until completion without blocking the client.
* **Dual Hosting Modes:** Can run as a standard `stdio` MCP server or an `http` MCP server.
* **Native AOT:** Pre-compiled native binaries mean incredibly fast startup times, low memory usage, and no runtime dependencies needed.

## 🏗️ Architecture

1. **MCP Client** connects to **Agent MCP Server**.
2. Client asks to run the `agent` tool (e.g., "Use the 'Coder' agent to refactor this project").
3. The Server spins up an LLM chat loop for the "Coder" agent.
4. The "Coder" agent has access to downstream MCP servers (e.g., a local filesystem MCP).
5. The agent acts autonomously, fetching files, reading, and writing, subject to your configured approval policies.
6. Once finished, the final result is returned to the original MCP client.

## 🚀 Getting Started

Because Agent MCP Server is compiled with Native AOT, there are **no prerequisites** or SDKs required to run it.

### Installation

Download the latest standalone binary for your operating system from [the releases page](https://github.com/KubaZ2/agent-mcp/releases/latest).

### Running the Server

You can run the server via standard I/O (default) or HTTP. You must provide a configuration, for example you can use a file for configuration and specify it via the `--config` flag.

```bash
# Run as stdio (default)
./agent-mcp --config config.ini
# this is equivalent to ./agent-mcp stdio --config config.ini

# Run as http server
./agent-mcp http --config config.ini
```

## ⚙️ Configuration

Agent MCP Server relies heavily on a configuration file to define LLM Providers, downstream MCP servers, and your Agents. Below is a comprehensive example using an `.ini` file format.

```ini
# Configure your LLM providers here.
# Supported Types: openai, anthropic, ollama

[Providers:claude]
Type = anthropic
ApiKey = sk-your-anthropic-api-key

[Providers:my-local-ollama]
Type = ollama

# Configure the MCP servers your agents can use.

# Example of a stdio-based downstream server
[Mcp:filesystem]
Command = npx
Args:0 = -y
Args:1 = @modelcontextprotocol/server-filesystem
Args:2 = /home/myuser/projects/myproject

# Example of an HTTP-based downstream server
[Mcp:websearch]
Endpoint = http://localhost:8080/mcp

# Define specialized agents exposed to the client.

[Agents:researcher]
Description = Use this agent to search the web and summarize extensive information.
Provider = my-local-ollama
Model = gemma4:12b
# Connects this agent to the websearch downstream MCP
Mcp:0 = websearch
# Automatically allow all tools for this agent
DefaultToolPolicy = Allow

[Agents:coder]
Description = Use this agent to read and modify local files.
SystemPrompt = You are a principal software engineer. Complete the task step by step.
Provider = claude
Model = claude-fable-5-1
# Connects this agent to the filesystem downstream MCP
Mcp:0 = filesystem
# Auto-approve safe read operations (supports glob and regex)
AutoApproveTools:0 = filesystem_read_*
AutoApproveTools:1 = /filesystem_list_.+/
# Auto-deny tools we don't want the agent using at all
AutoDenyTools:0 = filesystem_move_file
```

For more configuration options, please refer to the [Wiki](https://github.com/KubaZ2/agent-mcp/wiki/Configuration).

### Policy & Tool Filtering

The `DefaultToolPolicy` controls how the agent handles tool calls to downstream MCP servers:

* `Ask`: Default, the server uses MCP Elicitation to prompt the user in their client GUI.
* `Allow`: The agent executes the tool immediately.
* `Deny`: The tool is hidden from the agent and cannot be called.

You can bypass the default policy for specific tools using `AutoApproveTools` and `AutoDenyTools`. These fields support glob (e.g., `filesystem_read_*`) and regex patterns (indicated by wrapping the pattern in slashes, e.g., `/filesystem_read_.+/`). Note that the tools are prefixed with the MCP server name followed by an underscore (e.g., `filesystem_read_file`, `websearch_search`). That's how the tools are presented to the agent. This behavior can be configured or disabled per downstream MCP server. Refer to the [Wiki](https://github.com/KubaZ2/agent-mcp/wiki/Configuration) for more details.

Elicitation prompts allow the user to choose from four options:
- **Approve Once**: Approve this tool call for this agent, but ask again next time
- **Approve Always**: Approve this tool call for this agent and all future calls
- **Deny Once**: Deny this tool call for this agent, but ask again next time
- **Deny Always**: Deny this tool call for this agent and all future calls

## 🛠️ Exposed MCP Tools

Once running, the server exposes a single tool to the connected client:

* **`agent`**:
  * `agent` (string, enum): The specific agent to trigger (dynamically populated from your configuration, e.g., "researcher", "coder").
  * `prompt` (string): The task for the agent to perform.

## 📜 License

This project is released under the [MIT License](LICENSE)
