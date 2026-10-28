workspace "Agent reasoning" "How model reasoning flows through a durable agent run" {
    model {
        agentApp = softwareSystem "Agent Application" "Hosts agents whose turns run as Dapr workflows" {
            host = container "Agent Host" "Runs the agents and their workflows" ".NET and Dapr Workflow" {
                callerWorkflow = component "Caller Workflow" "Runs the agent with the previous turn's messages" "Dapr Workflow" "Client"
                agentRun = component "Agent Run Workflow" "Runs the LLM and tool loop and keeps the turn's messages" "Dapr Workflow" "Manager"
                callLlm = component "Call LLM Activity" "Sends this agent's reasoning back and captures the new reasoning" "Dapr Workflow Activity" "Engine"
                executeTool = component "Execute Tool Activity" "Runs one tool call" "Dapr Workflow Activity" "ResourceAccess"
                chatClient = component "Chat Client Access" "Calls the model and returns its reasoning" "Microsoft.Extensions.AI IChatClient" "ResourceAccess"
            }
        }
        llmProvider = softwareSystem "LLM Provider" "Generates answers, tool calls and reasoning" "External,Resource"

        callerWorkflow -> agentRun "Runs the agent with history"
        agentRun -> callLlm "Calls the model once"
        agentRun -> executeTool "Runs each requested tool"
        callLlm -> chatClient "Sends messages with reasoning"
        chatClient -> llmProvider "Requests a response" "HTTPS"
        agentApp -> llmProvider "Requests responses from"
    }
    views {
        component host "AgentHostComponents" {
            include *
            autoLayout
        }
    }
}
