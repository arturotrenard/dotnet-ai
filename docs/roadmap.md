# Roadmap: potenciar Diagrid.AI.Microsoft.AgentFramework

Análisis del 2026-10-28. Reúne dos revisiones hechas con verificación cruzada contra el código:

- **Parte A:** soporte de razonamiento (esfuerzo por agente y recuperar el texto del razonamiento).
- **Parte B:** ideas que dapr-agents (Python, org `dapr`) ya usa en producción y que podemos portar.

Referencias usadas:

| Repo | Ref |
|---|---|
| dotnet-ai (fork, `dev`) | `149b92e` |
| dapr-agents (`main`) | `1fd68b36` (2026-10-27) |
| dapr/dapr (`master`) | `9dcb2984` (2026-10-28) |
| components-contrib | `v1.19.0` / `main` `62dcbc76` |
| dotnet-sdk | `v1.19.0` (idéntico a `master` en `src/Dapr.AI*`) |
| MEAI / MAF | Microsoft.Extensions.AI 10.6.0 / Microsoft.Agents.AI 1.9.0 |

Las líneas `archivo:línea` se refieren a esas refs. Si el código cambió, vuelve a verificar antes de actuar.

**Contexto upstream:** siguen abiertos sin revisión en `diagridio/dotnet-ai` #96, #97, #98, #99 (timeout del LLM), #100 (Dapr 1.19), #101 (ejecuciones desacopladas) y #102 (binary store). Conviene no abrir una tanda grande de PRs nuevos; mejor uno chico y fácil de defender.

---

## Parte A: razonamiento (reasoning effort y reasoning content)

### Objetivos

1. Fijar el esfuerzo de razonamiento por agente, y opcionalmente por request.
2. Recuperar el texto del razonamiento y conservarlo en el historial y en el `AgentResponse`.

### Hallazgos verificados, por capa

**MEAI 10.6 (ya lo usamos)**
- `ChatOptions.Reasoning` es de tipo `ReasoningOptions`, con:
  - `Effort`: None, Low, Medium, High, ExtraHigh.
  - `Output`: None, Summary, Full.
- `TextReasoningContent` tiene `Text` y `ProtectedData`, con discriminador JSON `"reasoning"`.
- Ninguno de estos tipos es `[Experimental]`, así que no hace falta `#pragma`.
- `OpenAIChatClient` (chat completions) manda `reasoning_effort`, pero solo lee `reasoning_content` (convención DeepSeek/vLLM), **no `reasoning`**. Tampoco reenvía el razonamiento al modelo. El cliente de la Responses API sí hace las dos cosas.
- MAF 1.9 pierde el `Reasoning` de nivel agente cuando también hay `ChatOptions` por request. Se arregló en `microsoft/agent-framework#5463`; la primera versión que lo trae es dotnet-1.10.0.

**dotnet-ai (este repo)**
- No existe configuración de razonamiento por agente.
- `CallLlmActivity.BuildChatOptions` (`Runtime/CallLlmActivity.cs:172-193`) arma un `ChatOptions` nuevo con solo 5 campos: AllowBackgroundResponses, AdditionalProperties, ContinuationToken, ResponseFormat y Tools. **Las `ChatOptions` propias del agente se descartan**, salvo Tools (`Hosting/DaprAgentsBuilderExtensions.cs:585`).
- El esfuerzo por request también se pierde. `ChatClientAgentRunOptions.ChatOptions` no sobrevive la serialización de `AgentRunOptions` en `DaprAgentInvocation`/`CallLlmInput`: una prueba mostró que vuelve como un `AgentRunOptions` simple.
- **El razonamiento se pierde en `CallLlmActivity.ParseResponse`** (`:218-241`), que solo conserva el texto y las llamadas a tools. No llega a `WorkflowChatMessage` (Role, Content, FunctionCalls, FunctionResults) ni al `AgentResponse` (`AgentRunWorkflow.cs:115`, `DaprAgentInvoker.cs:284`, `SessionTurnStatus.ResponseText`).
- `ParseResponse` también descarta `Usage`, `FinishReason`, `ModelId` y `ResponseId`.
- Los DTO del workflow los serializa el `JsonDaprSerializer` de Dapr por reflexión, con los defaults Web, no con source-gen. Agregar propiedades `init` nullables es compatible con historiales viejos, que las leen como null.
- Una prueba confirmó que un `AgentResponse` con `TextReasoningContent` hace round-trip con los defaults Web. Ojo: `$type` tiene que ser la primera propiedad.

**Dapr (proto, runtime y components-contrib)**
- `ConversationRequestAlpha2` no tiene campo de esfuerzo. El TODO de `ReasoningEffort` está en `dapr/proto/runtime/v1/ai.proto:142`, igual en v1.19.0 y en master.
- `ConversationResultMessage` (`ai.proto:391-400`) solo tiene `content` y `tool_calls`. `ConversationMessageOfAssistant` tampoco lleva razonamiento.
- `parameters` no sirve como atajo: el **runtime** nunca lo lee en alpha2 (`pkg/api/universal/conversation.go`), y el proto dice que se va a eliminar.
- `metadata` sí llega al componente, pero langchaingo lo serializa como `metadata.reasoning_effort` dentro del body, no como campo de primer nivel. No sirve.
- `conversation.openai` usa **langchaingo**, no openai-go. langchaingo:
  - manda `reasoning_effort` solo a partir de un valor "strength" entre 0 y 1, que da low, medium o high (no permite none, minimal, xhigh ni max);
  - lee solo `reasoning_content`.
- `langchaingokit.NormalizeConverseResult` descarta el `ReasoningContent` de todos modos, y `WithThinkingMode` está comentado.
- components-contrib #4667 (reasoningEffort como metadata del componente) se cerró sin merge. #4670 (Responses API, `useResponsesAPI`) entró en 1.19. Ninguno resuelve esto: la Responses API descarta los reasoning items a propósito (`responses.go:53-56`).
- `DaprChatClient` (dotnet-sdk 1.19) ignora `ChatOptions.Reasoning`, aplana los mensajes a `message.Text` y no devuelve `TextReasoningContent` ni el uso de tokens.
- Nadie upstream (dapr, components-contrib, dotnet-sdk, docs, dapr-agents) está trabajando en esfuerzo por request ni en devolver el razonamiento.
- langchaingo tiene unos 190 PRs abiertos. El de `reasoning_effort` (#1129) está abierto desde 2025-04, así que no conviene depender de él.

**team_learning_agents**
- `DaprConversationChatClient` es un `IChatClient` propio (`Infrastructure/DaprConversationChatClient.cs`). Mapea Temperature, ToolMode, ConversationId, Tools y PromptCacheRetention, e ignora `Reasoning`.
- `Agents` en `appsettings.json` es un `Dictionary<string,string>` de agente a modelo (`AgentModels.cs:46-59`). Para el esfuerzo conviene una sección hermana, por ejemplo `AgentReasoning`.
- Usar aliases de modelo sobre el mismo componente no funciona, porque los clientes se registran por nombre de componente y chocan.
- El bake-off existente compara **modelos**, y solo para los roles Engineer y Researcher (`Program.cs:637-691`).
- Consume el fork como nupkg local `1.2.2-fork.N`. Cada cambio en el fork requiere volver a empaquetar y subir N.

### Pruebas reales contra Ollama Cloud (`glm-5.3:cloud`)

Respuestas guardadas en [`roadmap-fixtures/ollama-glm-5.3/`](roadmap-fixtures/ollama-glm-5.3/), sin la key.

| Prueba | Resultado |
|---|---|
| `r1_base` (sin parámetros) | El razonamiento viene en **`choices[0].message.reasoning`**, activo por defecto. No hay `<think>` en el contenido. |
| `r2_low` (`reasoning_effort: "low"`) | 4 tokens y **sin campo `reasoning`**. |
| `r3_high` / `r4_reasoning_obj` | `reasoning_effort: "high"` y `reasoning: {effort: "high"}` se comportan igual. El razonamiento salió **más corto** que sin parámetro. |
| `r5_none` (`"none"`) | ⚠️ El modelo sigue razonando, pero el razonamiento se mezcla con la respuesta y la arruina. **No exponer `None` para este modelo.** |
| `r6_stream` | El razonamiento va en **`delta.reasoning`**. |
| Uso de tokens | No hay `reasoning_tokens`: el razonamiento va sumado en `completion_tokens`. |

Son muestras de una sola llamada con un prompt trivial, así que no prueban que el esfuerzo cambie la calidad. Antes de definir un mapeo, consultar `/api/show` para `glm-5.3:cloud` y ver si maneja niveles o solo prende y apaga el razonamiento.

### Alternativas evaluadas

| Opción | ¿Funciona con Ollama y glm-5.3? | Veredicto |
|---|---|---|
| `parameters` de Dapr | No: el runtime no lo lee | Descartada |
| `metadata` de Dapr | No: queda anidado en `metadata` | Descartada |
| `useResponsesAPI` | No: descarta el razonamiento | Descartada |
| Parsear `<think>` | No aplica: glm-5.3 usa un campo aparte | Descartada |
| MEAI.OpenAI directo a `https://ollama.com/v1`, con un decorador que lea `message.reasoning` | Sí. El campo extra se lee con `completion.Patch.TryGetValue("$.choices[0].message.reasoning"u8, ...)`, el mismo mecanismo que usa MEAI para `reasoning_content` | Buena para medir ya |
| **OllamaSharp** (API nativa) | Probable. En su master, `AbstractionMapper.cs:86-95` mapea `Reasoning.Effort` a `think`, y `:401` devuelve `TextReasoningContent`. **Pendiente verificar** que esté en 5.5.0 y que `ollama.com/api/chat` acepte la key | Hacer una prueba corta |
| Cambios upstream en Dapr (proto, runtime, components-contrib, dotnet-sdk) | Sí, si el componente decodifica `reasoning` por su cuenta | Lo correcto a largo plazo, pero caro |

Saltarse el sidecar hace perder el YAML del componente, la key vía secret store, el tracing del sidecar y ScrubPII. Los agentes siguen siendo durables, porque igual corren dentro de `CallLlmActivity`.

### Plan recomendado (después de la crítica adversarial)

1. **Medir primero, solo en team_learning_agents, sin tocar el fork.**
   - Registrar clientes con claves por nivel de esfuerzo (`direct-glm-5.3-high`, ...), cada uno con un `DelegatingChatClient` que fija `options.Reasoning` y con un decorador de uso y cuota que replica `LlmUsage.Record`.
   - Agregar un rol Synthesizer o Strategist al bake-off, con N repeticiones por combinación de modelo y esfuerzo, y fijar antes un umbral mínimo de diferencia.
   - Probar contra los fixtures con un `HttpMessageHandler` falso, no con llamadas en vivo.
2. **Si el esfuerzo sirve, cambios en dotnet-ai** que funcionan con cualquier `IChatClient`:
   - Capturar el razonamiento en `ParseResponse` como **lista de bloques** `{Text, ProtectedData}`, no como un string concatenado.
   - Guardarlo con un **DTO de strings**: si guardamos `ReasoningOptions` tal cual, la historia queda con el esfuerzo como número (`{"effort":3}`).
   - **Dejarlo fuera de `CallLlmInput` y de `PriorMessages`.** Si no, el almacenamiento crece de forma cuadrática, porque cada vuelta del loop de tools vuelve a serializar toda la historia. Se adjunta al final, a `TurnMessages` y al `AgentResponse`, con un tope de tamaño.
   - Reenviar el razonamiento al modelo **siempre dentro del loop de tools de una misma ejecución** (Anthropic y DeepSeek lo exigen) y **solo si se activa explícitamente entre turnos**.
   - Dejarlo fuera del custom status (`SessionTurnStatus`) y de lo que reciben los context providers, salvo que se active explícitamente.
   - `WithReasoning(effort)` y `WithReasoning(agentName, effort)` siguiendo el patrón de `WithLlmTimeout`, con un aviso en el log si el cliente es `DaprChatClient` y lo va a ignorar.
   - Pasar `FinishReason` a `CallLlmOutput`: si viene `Length` con el texto vacío, es un error y no un éxito.
   - Esfuerzo por request (extraer `ChatClientAgentRunOptions.ChatOptions.Reasoning` antes de la frontera del workflow) solo cuando alguien lo necesite.
3. **Dapr upstream, al final.**
   - Abrir primero un issue en dapr/dapr.
   - Proto: `reasoning_effort` como **string** en el request, y `reasoning` en `ConversationResultMessage` y en `ConversationMessageOfAssistant`. Ojo, el TODO de `refusal` ya ocupa el campo 3.
   - En components-contrib, mandar el campo y decodificar `reasoning`/`reasoning_content` **en el propio componente**, con un wrapper HTTP como el del #4667, en vez de esperar a langchaingo.
   - Después dotnet-sdk (`ConversationOptions.ReasoningEffort`, `ResultMessage.Reasoning`, mapeo en `DaprChatClient`), y por último `DaprConversationChatClient` en la app.
   - `dapr run --runtime-path` sirve solo para los ejemplos. Aspire AppHost y Testcontainers necesitan su propio override.

### Decisiones (2026-10-28)

1. **Tomada:** los agentes que necesiten razonamiento pueden saltarse el sidecar con un cliente directo mientras Dapr no lo soporte.
2. **Tomada:** el razonamiento **se reenvía al modelo entre turnos**, no solo dentro del loop de tools. El objetivo es que cada request siga desde lo ya razonado y no empiece de cero. Esto reemplaza el "opt-in entre turnos" del plan de arriba.
3. **Tomada:** el historial de la conversación, razonamiento incluido, **se guarda fuera del historial del workflow**, en un store de sesiones estilo `DaprSessionStore` de dapr-agents (ver abajo). Esto reemplaza "adjuntarlo a `TurnMessages`" y resuelve el crecimiento cuadrático.
4. Pendiente: ¿los cambios en Dapr dependen del resultado del bake-off, o son un objetivo en sí mismo?
5. Pendiente: ¿se guarda `ProtectedData`? Ollama no lo usa; Responses y Anthropic sí. Mientras tanto se guarda, porque el store no tiene el problema de tamaño de los payloads del workflow.

### Cómo lo resuelve dapr-agents, y el diseño que adoptamos

dapr-agents tiene el mismo problema por el camino de Dapr Conversation (solo lee `reasoning_tokens`, `llm/dapr/client.py:287`). Sus clientes directos tampoco conservan el razonamiento: OpenAI lo descarta, y Anthropic guarda el *thinking* en `metadata['raw_content']` (`llm/anthropic/utils.py:430-523`), pero nada lo vuelve a leer. El `DurableAgent` no tiene opción de esfuerzo; solo pasa `messages`, `tools`, `response_format` y `tool_choice` (`agents/durable.py:2345-2355`).

Su solución para no empezar de cero es el **executor de Claude Agent SDK** (#809, 2026-10-01; último fix #902, 2026-10-27). Le delega el loop completo al SDK y guarda la **transcripción nativa de la sesión, thinking incluido, en un state store de Dapr** con `DaprSessionStore` (`agents/executors/dapr_session_store.py`). La sesión se retoma con el `session_id` en cualquier pod o en una activity reintentada. Formato:

- un **manifest** por sesión, en `{prefix}{session key}`, con la lista ordenada de IDs de chunks, una ventana acotada de IDs de entradas recientes (para appends idempotentes) y la fecha de modificación;
- **chunks inmutables** en `{manifest key}#{chunk id}`, de tamaño acotado (`max_chunk_bytes`); una entrada demasiado grande va en un chunk propio;
- el append escribe primero los chunks nuevos y después confirma el manifest **con ETag** (gana el primero). Quien pierde la carrera reintenta sobre el manifest nuevo y borra sus chunks huérfanos. Si el guardado llegó pero se perdió la respuesta, se comprueba contra el manifest guardado antes de borrar nada;
- mientras el último chunk ocupe menos de la mitad de `max_chunk_bytes`, el siguiente append lo reescribe junto con las entradas nuevas, así la cantidad de chunks crece con el tamaño y no con la cantidad de appends;
- **TTL** opcional: el manifest vence `ttl` después del último append, y los chunks se escriben con el doble para que siempre sobrevivan al manifest.

Solo funciona con Claude, así que para Ollama/glm-5.3 seguimos necesitando nuestra parte. Lo que adoptamos en dotnet-ai:

1. Un store de sesiones de conversación sobre `DaprStateManagementClient`, con el mismo formato de manifest, chunks, ETag, idempotencia y TTL. El workflow solo lleva el ID de la sesión.
2. Las activities cargan el historial del store antes de llamar al LLM y le agregan cada turno, con el razonamiento como `TextReasoningContent` (texto y `ProtectedData`). Las claves de idempotencia salen del ID de la instancia del workflow y del número de turno, así un reintento o un replay no duplica entradas.
3. `CallLlmActivity` captura el razonamiento de la respuesta y lo reenvía al modelo en los turnos siguientes.
4. `WithReasoning(...)` por agente, y aplicar las `ChatOptions` del agente (#3).
5. En team_learning_agents, un cliente directo a Ollama que mande `reasoning_effort` y convierta `message.reasoning` en `TextReasoningContent`.

El diseño detallado, con algoritmos, protocolo de commit, división en PRs y tests, está en [`conversation-store-design.md`](conversation-store-design.md). Sale de tres propuestas independientes evaluadas por tres jueces.

Precedente upstream para defenderlo: dapr-agents ya persiste las transcripciones de sesión en un state store de Dapr con este formato. Encaja también con la idea #23 de la Parte B (memoria durable en un state store).

---

## Parte B: ideas de dapr-agents para portar

Solo se cuenta una idea si está en el código de `dapr_agents/` en `main` (no un TODO ni una página de docs), y un escéptico verificó que dotnet-ai, MAF y el SDK de Dapr para .NET no la tienen ya. De 46 candidatas pasaron 40, que se agrupan en 26 ideas.

**Advertencia:** dapr-agents es de la org `dapr` y dotnet-ai es de `diagridio`. Los mantenedores se cruzan pero no son los mismos, así que el precedente ayuda pero no garantiza la aprobación.

### Ranking

| # | Idea | Evidencia en dapr-agents | dotnet-ai hoy | Valor | Esfuerzo |
|---|---|---|---|---|---|
| 1 | **Política de reintentos durable** para las activities del LLM y del contexto | `agents/configs.py:1043-1060`, `durable.py:461-483`. Activada por defecto (3 intentos, 5 s, ×1,5, máx 30 s) en ~40 llamadas | Ninguna activity tiene reintentos (`AgentRunWorkflow.cs:68,92,138,193`), así que un 429 hace fallar la ejecución | Alto | S |
| 2 | **`MaxIterations` configurable** y una señal de que se alcanzó | `configs.py:661`; `durable.py:1177` hace `set_custom_status("max_iterations_reached")` | `const MaxIterations = 20` fijo (`AgentRunWorkflow.cs:33`); no avisa cuando se agota | Alto | S |
| 3 | **Aplicar las `ChatOptions` del agente** en cada llamada (Temperature, MaxOutputTokens, ToolMode, ModelId, **Reasoning**) | `llm/dapr/chat.py:351-354`, `durable.py:2353` | Solo conserva Tools (`DaprAgentsBuilderExtensions.cs:585`) | Alto | S |
| 4 | **Uso de tokens, `FinishReason` y modelo en `AgentResponse`** (incluye tokens de razonamiento y cacheados) | `llm/dapr/client.py:283-319`, `durable.py:2484-2495` | `ParseResponse` los descarta | Alto | S |
| 5 | **Versionado de `AgentRunWorkflow`/`SessionWorkflow`** | `durable.py:4133-4153`, `docs/development/README.md:422-435` | Sin versión; el SDK ya trae `[WorkflowVersion]` e `IsPatched`. **Es requisito para #9, #11 y #12** | Medio (habilitador) | M |
| 6 | **Cliente de Dapr Conversation completo**: IDs de las llamadas a tools, JSON schema de las tools, `response_format`, uso de tokens, modelo | `llm/dapr/chat.py:195-273, 404-432, 495-560` | El `DaprChatClient` del SDK 1.19 pierde todo eso | Alto | M |
| 7 | **Metadatos de Catalyst según el esquema JSON**, con un test golden. **Incluye un bug** | `schemas/agent-metadata/v1.0.0.json`, `tests/agents/test_metadata_schema_golden.py` | `Catalyst/ToolMetadata.cs:26` escribe `"arguments"`, pero el esquema exige `args`; falta `memory` | Medio | S |
| 8 | **Índice del registro de Catalyst**: merge con ETag, claves por equipo, desregistro opcional al apagar | `agents/components.py:621-956` | Sobrescribe `agents:default:_index`, así que dos apps se pisan | Medio | M |
| 9 | **Aprobación de tools durable**: esperar un evento externo con timeout | `durable.py:1221-1338`, rutas `/hitl/approvals` | `IToolApprovalHandler` bloquea dentro de la activity, sin timeout | Alto | M |
| 10 | **Filtros de política para llamadas a tools** (Proceed/Deny/Skip/Mutate/RequireApproval) | `hooks.py:19-265`, `durable.py:711-830` | Solo aprobar o denegar | Medio | S |
| 11 | **Ejecución de tools en paralelo** (por defecto, secuencial) | `types/agent.py:42-55`, `durable.py:995-1062` | `foreach` secuencial (`AgentRunWorkflow.cs:128-141`) | Medio | S |
| 12 | **Agentes como tools**, ejecutados como child workflows, incluso entre apps | `tool/workflow/agent_tool.py:118-319`, `examples/08-agents-as-tools` | Solo se puede componer desde código del usuario | Alto | M-L |
| 13 | **Spans de OTel** `execute_tool` e `invoke_agent` | `observability/wrappers/*`, `constants.py:37-64` | No hay `ActivitySource` | Alto | M |
| 14 | **Salida estructurada de punta a punta**: `ResponseFormat` desde `T`, schema saneado y errores claros | `llm/utils/structure.py`, `chat.py:495-560` | `RunAgentAndDeserializeAsync<T>` no fija `ResponseFormat` | Medio | M |
| 15 | **Servidores MCP de Dapr como tools**, vía child workflows `CallTool` | `tool/mcp/dapr_workflow_client.py:57-96` | Nada; el SDK .NET no tiene `DaprMCPClient` | Alto | M |
| 16 | **API de ciclo de vida de la ejecución** (start, status, terminate, purge) y `MapDaprAgent` para HTTP | `workflow/runners/agent.py:911-1270` | No hay purge ni terminate; los ejemplos repiten a mano el endpoint de status | Medio | M |
| 17 | **Agentes disparados por pub/sub** | `runners/agent.py:786-930`, `workflow/utils/subscription.py` | Nada | Medio | M |
| 18 | **Tool `ask_user` durable** | `tool/workflow/ask_user_tool.py` (depende de streaming, que está en ALPHA) | Solo se puede esperar entre turnos | Medio | M |
| 19 | **IDs idempotentes** para los workflows que una tool agenda desde una activity | `tool/workflow/activity_bridge.py:120-180` | `Guid.NewGuid()`, que duplica si la activity se reentrega | Medio | S |
| 20 | **Completar los metadatos de Catalyst** (Memory, modelo) | `agents/base.py:724-786` | El modelo siempre es `"unknown"` | Bajo-Medio | S |
| 21 | **Descubrir el componente de Conversation** y validarlo al arrancar | `llm/dapr/chat.py:106-112, 563-591` | Un nombre equivocado solo falla dentro de la activity | Bajo | S |
| 22 | **Nombre de workflow compatible entre frameworks** (`dapr.microsoft-agent-framework.<agent>.workflow`) | `tool/workflow/agent_tool.py:37-106` | Un par en Python no puede invocar un agente .NET | Medio (solo despliegues mixtos) | M |
| 23 | **Memoria de conversación durable en un state store** | `memory/daprstatestore.py` | El log de la sesión vive en el estado del workflow y crece sin límite | Medio | L |
| 24 | **Streaming de tokens por pub/sub** | `streaming/*` (ALPHA) | Nada | Medio | L |
| 25 | **Hot-reload de configuración** desde un Configuration store | `agents/base.py:820-1000` (con riesgo de replay upstream) | Nada | Bajo-Medio | M |
| 26 | **Orquestadores multi-agente integrados** | `agents/orchestration/*` (poco extensibles) | Nada; MAF Workflows corre solo en proceso | Medio | L |

### Detalles de implementación que importan

- **#1 y #2:** resolver la configuración en `ResolveAgentContextActivity` y devolverla en su output, así queda en el historial y el replay es seguro. No leer DI ni configuración dentro del cuerpo del workflow. Usar `?? 20` para que los historiales en curso sigan reproduciéndose. En #1, dejar `ExecuteToolActivity` sin reintento, porque ya convierte las excepciones en resultados. Documentar que en el peor caso la latencia es intentos × timeout.
- **#2:** marcar el agotamiento con un flag explícito. No copiar el chequeo `turn == max` de dapr-agents, que marca como agotada una respuesta final válida.
- **#3:** clonar las `ChatOptions` del agente (sin Tools) en `ChatClientRegistry` y usarlas como base en `BuildChatOptions`. Todo pasa dentro de la activity, así que no cambia la serialización.
- **#12 y #19:** usar IDs deterministas, como `"{InstanceId}:at:{CallId}"`, y no uuid4.

### Lo que no conviene traer

- Orquestadores (#26), hot-reload (#25) y streaming (#24): son grandes, están en ALPHA o tienen riesgo de replay.
- Compactación de historial: dapr-agents no la hace, y MAF ya trae `CompactionProvider` e `IChatReducer`.
- Recall vectorial: dapr-agents no lo usa de verdad, y MAF ya trae `ChatHistoryMemoryProvider`.
- Helpers de ingestión para RAG: no son específicos de Dapr y ya los cubren MEAI y VectorData.
- Detalles de dapr-agents que no hay que copiar:
  - la variable de entorno `DAPR_API_MAX_RETRIES`;
  - el hack de temperature=1;
  - el slot `after_tool_call`, que nunca se dispara;
  - el modo SEQUENTIAL que crea todas las tasks de entrada;
  - IDs uuid4 para child workflows;
  - leer `max_iterations` dentro del cuerpo del workflow.

### Relación con la Parte A

- **#3** es la parte de la Parte A que tiene precedente en dapr-agents. Se defiende upstream como "aplicar los parámetros del modelo del agente", no como "soporte de razonamiento".
- **#4** trae el conteo de tokens de razonamiento.
- dapr-agents **no** guarda ni reenvía el razonamiento. Perder los bloques de thinking firmados de Anthropic cuando el agente usa tools es un bug real de dotnet-ai, y habría que mandarlo como bug fix independiente.

---

## Orden de trabajo propuesto

Actualizado el 2026-10-28: el razonamiento pasa adelante, porque es lo que necesita team_learning_agents.

1. **#7:** el bug de `args` en Catalyst. ✅ PR #5 en el fork.
2. **Historial de sesión durable con razonamiento**, estilo `DaprSessionStore` (ver "Cómo lo resuelve dapr-agents" en la Parte A). Plan en `docs/conversation-store-design.md` §6:
   - PR 1, capturar y reenviar el razonamiento. ✅ PR #6 en el fork.
   - PR 2, aplicar las `ChatOptions` del agente (#3), `WithReasoning` y `WithReasoningReplay`. ✅ PR #7 en el fork.
   - PR 3, el store (`WithConversationStore`, `IConversationStore`), todavía sin conectar a los runs. En curso.
   - PR 4, correr agentes contra una conversación guardada (`RunAgentInConversationAsync`, `AttachConversation`).
   - PR 5, `SessionWorkflow` respaldado por el store.
3. Cliente directo a Ollama en team_learning_agents y bake-off de esfuerzo.
4. **#1:** reintentos durables.
5. **#2:** `MaxIterations` configurable y la señal de agotamiento.
6. **#4:** uso de tokens y `FinishReason`.
7. **#5:** versionado de workflows, antes de #9, #11 o #12.
8. Lo demás según los resultados y lo que vaya entrando upstream.

Cada punto va primero al fork (`dev`) y después, si aplica, a upstream desde una rama basada en `upstream/master`, con un PR chico.

## Pendiente de verificar

- Que Ollama Cloud corra el mismo código de compatibilidad OpenAI que Ollama open source. Solo las pruebas en vivo de arriba están verificadas.
- Si el esfuerzo de verdad cambia la profundidad o la calidad del razonamiento en glm-5.3.
- ~~Si guardar con first-write y sin ETag solo crea la clave (§8.1 del diseño).~~ Medido contra daprd 1.19 y Redis en el PR 3: solo crea, pero Redis reporta el conflicto como `Internal`, no `Aborted`.
- Que OllamaSharp 5.5.0 traiga el mapeo de `Reasoning`, y que `ollama.com/api/chat` acepte la key con bearer.
- Si los mantenedores de dapr/dapr exigen una propuesta formal para cambiar el proto.
- ~~La sobrecarga de `WithAgent` con fábrica nombrada se saltaba `RegisterAgentComponents`.~~ Confirmado y corregido en el PR #7, con test.
