# Claude Code — este proyecto

Las reglas operativas del repo (build/deploy, contador de build, consulta
del SDK) viven en **[`AGENTS.md`](AGENTS.md)**. Léelo y síguelo; no hay
una segunda copia aquí.

Para trabajo que toque la API de X-Plane (`XPLM*`/`XP*`, datarefs,
widgets, etc.), usa además la skill
[`.claude/skills/xplane-sdk/SKILL.md`](.claude/skills/xplane-sdk/SKILL.md).

Para inspeccionar telemetría / diagnosticar un vuelo o maniobra a partir
del registro, usa
[`.claude/skills/caja-negra/SKILL.md`](.claude/skills/caja-negra/SKILL.md)
(“inspecciona la caja negra”, DataLog, temblores, G, intercept…).

Para controlar la UI desde el agente (driver HTTP, takeoff, maniobras,
intercept, reset simulación LEBL, iterar pruebas), usa
[`.claude/skills/aicopilot-driver/SKILL.md`](.claude/skills/aicopilot-driver/SKILL.md).
