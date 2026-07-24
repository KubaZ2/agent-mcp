using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

Console.WriteLine("Starting AgentMcpTest...");

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
    // Use LogLevel.Trace if you want to see the raw JSON-RPC messages
    builder.SetMinimumLevel(LogLevel.Debug);
});

var clientTransport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command = "dotnet",
    Arguments = ["AgentMcp.dll"],
    WorkingDirectory = "../../../../AgentMcp/bin/Debug/net10.0"
}, loggerFactory);

// var clientTransport = new HttpClientTransport(new()
// {
//     Endpoint = new("http://localhost:5000"),
// }, loggerFactory);

McpClientOptions clientOptions = new()
{
    Capabilities = new()
    {
        Elicitation = new()
        {
            Form = new(),
        },
    },
};

var client = await McpClient.CreateAsync(clientTransport, clientOptions, loggerFactory);

// Print the list of tools available from the server.
foreach (var tool in await client.ListToolsAsync())
{
    // tool.CallAsync()
    // tool.CallAsync()
    // tool.InvokeAsync()
    Console.WriteLine(tool.ProtocolTool.InputSchema);
    Console.WriteLine($"Tool: {tool.JsonSchema}");
    Console.WriteLine($"{tool.Name} ({tool.Description})");
}

// Execute a tool (this would normally be driven by LLM tool invocations).
// var result = await client.CallToolAsync("get_name");

// var result = await client.CallToolWithPollingAsync(new()
// {
//     Name = "get_name",
//     InputResponses = new Dictionary<string, InputResponse>
//     {
//     }
// });

await Task.Delay(1000);

var task = await client.CallToolAsTaskAsync(new()
{
    // Name = "get_name",
    Name = "run_agent",
    Arguments = new Dictionary<string, JsonElement>
    {
        ["agent"] = JsonSerializer.SerializeToElement("code-writer"),
        ["instruction"] = JsonSerializer.SerializeToElement("Write a haiku about C#."),
    }
});

var id = task.TaskCreated!.TaskId;

// client.ListToolsAsync().Result[0].JsonSchema

while (true)
{
    var taskResult = await client.GetTaskAsync(id);

    if (taskResult is InputRequiredTaskResult { InputRequests: { } inputRequests })
    {
        Dictionary<string, InputResponse> inputResponses = new(inputRequests.Count);
        foreach (var inputRequest in inputRequests)
        {
            Console.WriteLine($"Input request: {inputRequest.Key} ({inputRequest.Value.ElicitationParams?.Message})");
            Console.Write("Enter response: ");
            var userInput = Console.ReadLine() ?? "";
            var element = JsonSerializer.SerializeToElement(new { action = userInput });
            Console.WriteLine($"Parsed JSON element: {element}");
            inputResponses[inputRequest.Key] = new InputResponse
            {
                RawValue = element,
            };

            await client.UpdateTaskAsync(new()
            {
                TaskId = id,
                InputResponses = inputResponses,
            });
        }
    }
    else if (taskResult is CompletedTaskResult completedTaskResult)
    {
        Console.WriteLine($"Task completed with result: {completedTaskResult.Result}");
        break;
    }

    // var status = taskResult.Status;
    //
    // Console.WriteLine(status);
    //
    // if (status == McpTaskStatus.InputRequired)
    // {
    //     if (taskResult is InputRequiredTaskResult inputRequiredTaskResult)
    //     {
    //         // inputRequiredTaskResult.InputRequests
    //         foreach (var inputRequest in inputRequiredTaskResult.InputRequests!)
    //             Console.WriteLine($"Input request: {inputRequest.Key} ({inputRequest.Value.ElicitationParams.Message})");
    //     }
    //     // Console.WriteLine(taskResult.StatusMessage);
    //     // Console.WriteLine(taskResult.ResultType);
    //     // taskResult.Meta
    // }

    await Task.Delay((int)taskResult.PollIntervalMs.GetValueOrDefault(1000));
}

// client.UpdateTaskAsync()

// echo always returns one and only one text content object
// Console.WriteLine(result.Content.OfType<TextContentBlock>().First().Text);
