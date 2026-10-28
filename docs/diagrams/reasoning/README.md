# Reasoning call chain

`workspace.dsl` models the components a durable agent run uses to keep and resend model
reasoning, with IDesign roles. `call-chain.md` is the call chain drawn from it, in the
README's "Model reasoning" section. Do not edit `call-chain.md` by hand: change the workspace
and regenerate it with the `architecture-diagram` skill:

```bash
docker run --rm --pull never -u "$(id -u):$(id -g)" -v "$PWD":/usr/local/structurizr \
    structurizr/structurizr export -workspace /usr/local/structurizr/workspace.dsl \
    -format json -output /usr/local/structurizr
python3 <skill>/scripts/call-chain.py --json workspace.json --from "Caller Workflow" \
    --title "Model reasoning in an agent run" -o call-chain.md
```
