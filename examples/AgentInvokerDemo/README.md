# Dapr .NET SDK - Agent Invoker Demo

This example demonstrates invoking a registered AI agent directly from DI while the execution
still runs inside Dapr Workflow activities under the hood.

## Prerequisites

- [.NET 8+](https://dotnet.microsoft.com/download) installed
- [Dapr CLI](https://docs.dapr.io/getting-started/install-dapr-cli/)
- [Initialized Dapr environment](https://docs.dapr.io/getting-started/installation)
- [Dapr .NET SDK](https://docs.dapr.io/developing-applications/sdks/dotnet/)
- An [Ollama Cloud](https://ollama.com) API key (create one at https://ollama.com/settings/keys) —
  no local Ollama install is needed. `Components/conversation-ollama.yaml` points at
  `https://ollama.com/v1` with the `gpt-oss:20b` model.

## Running the example

From the `\examples\` directory, export your key and start the Dapr runtime in the same shell
(the sidecar reads it through `Components/envvar-secretstore.yaml`):

```sh
export OLLAMA_API_KEY="your_api_key"
dapr run --app-id invokerapp --dapr-grpc-port 50001 --dapr-http-port 3500 --resources-path "Components/"
```

Then run the app in another terminal with `dotnet run`. It listens on `http://localhost:5041`.

### Ask for a normal response

Send a POST request to `http://localhost:5041/ask`:

```json
{
  "prompt": "Give me three reasons to use workflow activities for AI calls."
}
```

You should receive a JSON response containing the agent's text.

### Ask for a structured response

Send a POST request to `http://localhost:5041/ask-typed`:

```json
{
  "prompt": "Summarize why deterministic workflows matter in one sentence."
}
```

This endpoint requests JSON output and deserializes it using a source-generated serializer context.

### Observing workflow-backed execution

Watch the application logs while making requests. The invoker logs

```
Running agent 'InvokerAgent' (chat client key '...', message length ..., has session False, has options False)
```

before it schedules the `AgentRunWorkflow`. The sidecar log then shows that workflow instance
completing, which is the evidence that the call ran through Dapr Workflow even though it was
invoked via DI. A missing or wrong key surfaces as `401: Unauthorized` from the
`CallLlmActivity`.
