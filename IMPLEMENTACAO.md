# FanCurve: plano de implementação (métricas + painel)

Versão 3, 12/11/2026.

A v2 incorporou duas revisões externas, conferidas contra o código da LibreHardwareMonitor v0.9.6 (§14). A v3 fecha o formato de armazenamento antes de gravar o anel: slot alinhado a 8 bytes, um único escritor, troca de layout sem o leitor ficar no arquivo antigo, `Volatile` no endereço mapeado, `samplesAt100`, bytes privados, média em `float`, relógio para trás e buraco no gráfico via `SignalXY`.

**Status:** o serviço está em `src/FanCurve` e o painel em `src/FanCurvePanel`. A fase 3 é só leitura e a fase 4 edita a curva na aba Fans. A medição ociosa de 30 min está na §12. A tentativa em jogo de 12/11 parou aos 21 min, com a GPU ociosa, então essa metade continua pendente. Temperaturas da CPU e fans da placa-mãe continuam sem leitura.

## 0. Já implementado (após a v1 deste plano)

| Item | Onde |
|---|---|
| Migração para **.NET 10 (LTS)**. O .NET 8 perdeu suporte em 10/11/2026 | `FanCurve.csproj`, `FanCurve.Core.csproj` |
| **Histórico interno da LHM desligado.** Ela guarda 1 dia em *cada* sensor, numa `List` que remove do início | `HardwareHub` zera `ValuesTimeWindow`, também no `HardwareAdded` |
| **Volta de suspensão/hibernação.** Detecta o salto do relógio de parede, zera suavização, histerese e rampa, e regrava o PWM na hora | `FanService.Step` (`resumeGapSeconds`) + `RuleRuntime.Reset` |
| **Regravação periódica do PWM.** O chip volta à BIOS depois da suspensão, e a LHM não dispara a escrita se o valor não mudou | `HardwareHub.Write`: um empurrão de 0,1% e o valor de volta (`reassertSeconds`) |
| Curva sem alocação por ciclo: agrega pela temperatura máxima, suaviza, aplica histerese, piso e rampa | `RuleRuntime` |
| GC de workstation sem coleta concorrente; ReadyToRun na publicação; uma coleta compactadora após a inicialização | `FanCurve.csproj`, `HardwareHub.Open` |

## 1. Objetivo

Acrescentar ao FanCurve:

- histórico de temperaturas, RPMs, % das fans e uso de CPU/GPU;
- um painel que abre só quando você quer e mostra os valores atuais, os gráficos ao longo do tempo e as estatísticas.

Tudo isso **com o menor custo possível de CPU, memória e disco** no processo que fica rodando o tempo todo.

**Princípios**

1. **O processo de 2º plano faz o mínimo.** Ele lê os sensores, controla as fans e grava números. Não desenha nada e não abre porta de rede.
2. **O painel só gasta recursos enquanto está aberto.** Fechado, custa zero. Ele nunca toca no hardware: só lê arquivos.
3. **Os dados têm formato fixo**, e o armazenamento foi desenhado para isso: binário, registros de tamanho fixo, só de anexar no fim. Não usa banco de dados.
4. **Medir antes e depois de cada otimização.** Os números deste documento são estimativas até serem medidos no PC real.

## 2. Arquitetura

```
┌──────────────────────── FanCurve.exe (tarefa agendada, SYSTEM) ─────────────────────────┐
│  PeriodicTimer 1 s                                                                      │
│   ├─ atualiza SÓ o hardware necessário (LibreHardwareMonitorLib + PawnIO)               │
│   ├─ regras: suavização → histerese → curva → piso → rampa → PWM   (controle das fans)  │
│   ├─ detecta volta de suspensão; regrava o PWM a cada 30 s          (já implementado)   │
│   ├─ 1 amostra no anel mapeado ──► data\recent-<N>.bin (24 h, 1 s; idx escolhe o N)    │
│   ├─ writer.lock exclusivo: a segunda instância sai                                     │
│   ├─ min/méd/máx do minuto; 1 registro/min ────────────► data\hist\AAAA-MM.fch          │
│   ├─ batimento + estado das regras no cabeçalho do anel                                 │
│   └─ eventos raros (fail-safe, sensor perdido, suspensão, início/fim) ► logs\events.log │
│  a cada 5 s compara a data de modificação do config\fancurve.json → valida → recarrega  │
└─────────────────────────────────────────────────────────────────────────────────────────┘
                          ▲ só leitura (sem servidor, sem porta)
┌──────────── FanCurvePanel.exe (usuário comum, abre quando quer) ────────────┐
│  "Agora": relê recent.idx; se a geração mudou, reabre o mapa (janela visível) │
│  "Histórico": gráficos e a tabela do período (média, máximo, p95)            │
│  "Fans": mostra as curvas do fancurve.json com o ponto atual                  │
│  Editar curva (fase 4) → apply-config em Program Files, elevado (UAC)         │
└───────────────────────────────────────────────────────────────────────────────┘
```

**O controle das fans continua 100% no FanCurve.exe.** O painel aberto, fechado ou travado não interfere no controle.

## 3. Decisões e alternativas descartadas

| Decisão | Por quê | Descartado |
|---|---|---|
| Painel lê arquivos, sem servidor | Sem porta aberta. O histórico continua legível com o FanCurve parado | Servidor HTTP em `localhost` |
| "Ao vivo" via **arquivo mapeado em memória** (anel) | Gravar e ler é escrever/ler memória. O Windows descarrega no disco aos poucos. O índice `recent.idx` é que troca de arquivo | Polling de banco; pipe nomeado; renomear o `.bin` que está mapeado |
| Histórico longo em **binário próprio**, mensal, registros fixos | ~150 bytes/min anexados; busca por data = cálculo de posição; apagar meses = apagar arquivo | SQLite (biblioteca embutida, sem instalação, mas mais pesada para este caso: fica como plano B) |
| Valores em `Half` (16 bits) | Metade do espaço de `float`, com precisão de sobra (±0,06 °C entre 64 e 128 °C; ±2 RPM a 3000 RPM) | `float`; inteiro com escala por canal |
| Painel **WinForms + ScottPlot 5** (`SignalXY`) | A interface nativa mais leve do .NET. `SignalXY` usa o `unixMs` real, então um buraco não precisa de um `NaN` por segundo | WPF, Avalonia/Electron, ícone residente na bandeja; `Signal` com espaçamento fixo |
| Projeto **Core** compartilhado | O painel não carrega a LHM nem o driver | Painel referenciar tudo |
| **.NET 10 (LTS)** | Suportado até nov/2028; a LHM 0.9.6 já tem build `net10.0`; traz `Environment.CpuUsage` | .NET 8 (fora de suporte desde 10/11/2026) |
| Recarga de config por **polling da data de modificação** (5 s) | Um `stat` a cada 5 s custa nada e nunca perde evento | `FileSystemWatcher` (perde eventos quando o buffer estoura e dispara várias vezes por gravação) |

## 4. Sugestões de eficiência, por prioridade

| # | Sugestão | Impacto | Esforço | Status |
|---|---|---|---|---|
| E0 | **Desligar o histórico interno da LHM** (`ValuesTimeWindow = Zero` em todos os sensores, inclusive os adicionados depois) | **Alto**: evita dezenas de MB após 24 h e um custo de CPU que cresce, porque a `List.RemoveAt(0)` desloca a lista inteira | Baixo | ✅ feito |
| E1 | **Atualizar só o hardware usado**: montar a lista de `IHardware` com sensores usados (regras + métricas, inclusive sub-hardware como o Super I/O) e chamar `Update()` só neles | **Alto** | Baixo | ✅ feito |
| E2 | **Laço sem alocação**: arrays pré-resolvidos, sem LINQ e sem strings por ciclo | Médio | Baixo | parcial (`Aggregate` ✅) |
| E3 | **Log só de eventos**: remover a linha de status de 30 s | Médio (disco) | Baixo | ✅ feito |
| E4 | GC de workstation sem a thread concorrente; ReadyToRun; `GC.Collect` compactador uma vez após a inicialização | Médio | Baixo | ✅ feito |
| E5 | `PeriodicTimer` em vez de `Task.Delay` | Baixo | Baixo | ✅ feito |
| E6 | **Sensores lentos em ritmo lento**: discos a cada 30 s, repetindo o último valor nos slots intermediários | Médio, se registrar discos | Baixo | ✅ feito |
| E7 | **Medir o próprio custo**: `Environment.CpuUsage` (.NET 9+) e bytes privados via `GetProcessMemoryInfo` no processo atual (`PrivateUsage`), a cada 60 s, gravados como canais. **Não** usar `Process.GetCurrentProcess()`: no Windows essas propriedades passam por `NtQuerySystemInformation` e leem a tabela de todos os processos. `Environment.WorkingSet` é barato, mas não é o número da §12 | Nenhum (é a ferramenta) | Baixo | ✅ feito |
| E8 | **CPU sem trocar de núcleo.** O `IntelCpu.Update()` da LHM lê temperatura e clock de cada núcleo trocando a afinidade da thread 2× por leitura (~24 trocas/s no i5-12400F). Ela não permite ligar só os sensores de pacote. Opções, em ordem: (a) atualizar o hardware de CPU a cada 2 s; (b) desligar o grupo de CPU da LHM e ler direto, com a classe pública `IntelMsr`, a temperatura do pacote (`IA32_PACKAGE_THERM_STATUS` + `IA32_TEMPERATURE_TARGET`, sem trocar núcleo) e o uso total via `GetSystemTimes`. A opção (b) vale só para Intel e perde as métricas por núcleo | A medir com a E7 | (a) baixo, (b) médio | fase 5, só se medido |
| E9 | **EcoQoS**. O i5-12400F não tem núcleos E, então o ganho é pequeno. Opção desligada por padrão | Baixo | Baixo | fase 5 |
| E10 | **Native AOT**. A LHM usa WMI/`System.Management` e reflexão; trimming pode quebrar a detecção. Só como experimento | Alto se funcionar | Incerto | fase 5 |
| E11 | **GPU a cada 2 s com a placa fria.** O `NvidiaGpu.Update()` lê tudo de uma vez (NvAPI + D3DKMT) e não dá para escolher sensores. Fria = abaixo de X °C; quente volta a 1 s | A medir | Baixo | fase 5, só se medido |

**O que não fazer (e por quê)**

- **`timeBeginPeriod`** (timer de alta resolução): aumenta o consumo do sistema todo.
- **Prioridade abaixo do normal**: sob carga de jogo, atrasaria justamente a subida das fans.
- **`EmptyWorkingSet` / `SetProcessWorkingSetSize`**: só faz o número "Working Set" parecer menor. A memória comprometida não muda, as páginas vão para a lista de espera ou para o pagefile e voltam com faltas de página. O Windows já faz isso sozinho quando falta memória.
- **`<EnableDiagnostics>false</EnableDiagnostics>` no `.csproj`**: essa propriedade não existe e seria ignorada em silêncio. O mecanismo real é a variável de ambiente `DOTNET_EnableDiagnostics=0`, que a tarefa agendada não define sem um script intermediário. O ganho é pequeno e o custo é perder a capacidade de diagnosticar o processo. Pulamos.

## 5. Formatos de arquivo

Todos são little-endian, com versão no cabeçalho. Valor ausente ou inválido é gravado como `Half.NaN`.

### 5.1 Tabela de canais (comum aos dois formatos)

| Campo | Tipo | Observação |
|---|---|---|
| `kind` | u8 | 1 temperatura °C, 2 RPM, 3 %, 4 carga %, 5 potência W, 6 memória GB, 7 relógio MHz, 99 interno |
| `name` | 48 bytes UTF-8 | Nome amigável |
| `sourceId` | 96 bytes UTF-8 | Identificador da LHM, ou `fancurve:cpu`, `fancurve:mem`, `rule:<nome>:out` |

**Layout hash**: FNV-1a 64 dos bytes da tabela, nessa ordem. Se a config ou o hardware mudar a lista de canais, abre-se um arquivo novo. O painel junta os canais pelo `sourceId`. Nome e `sourceId` são cortados na fronteira de um caractere Unicode, com o resto zerado.

### 5.2 Anel de 24 h, 1 s

Dois arquivos em `data\`:

- `recent.idx` (64 bytes): ponteiro do arquivo vigente. `magic "FCRI"`, `version u16` (1), `generation u32`, `layoutHash u64`, nome (`recent-00000001.bin`). A troca é `.tmp` + `Flush(flushToDisk: true)` + `File.Move` na mesma pasta.
- `recent-<generation>.bin`: o anel. Não é renomeado enquanto pode estar mapeado. Layout novo cria outro arquivo e só então atualiza o `recent.idx`. O painel relê o índice a cada 1 s e, se a geração mudou, fecha o mapa e abre o arquivo novo. Binários que deixaram de ser o vigente são apagados na partida seguinte; se ainda estiverem mapeados, a exclusão falha e tenta de novo depois.

O escritor abre o binário com `FileShare.ReadWrite | FileShare.Delete` e mapeia com `MemoryMappedFile` sem nome. O leitor abre com `FileAccess.Read` e o mesmo share. O arquivo nasce no tamanho final (`SetLength`); não cresce. Com o mesmo `layoutHash`, o processo seguinte continua do `writeCount` e não zera as 24 h.

`writer.lock`, com `FileStream.Lock` exclusivo, impede a segunda instância (tarefa SYSTEM e modo console). O lock vale entre sessões. Um mutex `Global\` exigiria `SeCreateGlobalPrivilege`, que o usuário do console não tem.

- **Cabeçalho** (versão 1), tamanho múltiplo de 4096. Inteiros de 64 bits ficam em offset múltiplo de 8:
  - `magic "FCRB"`, `version u16`, `channelCount u16`, `slotCount u32` (86400), `slotSize u32`, `intervalMs u32`, `headerSize u32`
  - `layoutHash u64`, `writeCount u64`, `processStartUnixMs i64`, `lastTickUnixMs i64`
  - `pid u32`, `resumeCount u32`, `configHash u64`, `failsafeMask u32`, `generation u32`, `ruleCount u16`, `ruleOutput[16] Half`
  - Tabela de canais.
- `writeCount` é o **número de amostras completas**. A próxima vai para o slot `writeCount % slotCount`. A última completa é a `writeCount − 1`.
- **Slot**: `unixMs i64` + `Half × channelCount` + padding até múltiplo de 8. Com 24 canais são 56 bytes, **≈ 4,8 MB**. Com 25 canais o cru seria 58; o padding leva a 64, e o `unixMs` do slot seguinte continua atômico.
- No máximo 16 regras. O `check` recusa uma config com mais, porque o cabeçalho tem `ruleOutput[16]`.

**Protocolo sem trava**, de um escritor para N leitores. O `Volatile` lê e grava o `writeCount` **no cabeçalho mapeado**, não numa cópia em campo managed.

| Lado | Passos |
|---|---|
| Escritor | 1) grava os valores e o `unixMs` no slot `writeCount % slotCount`; 2) grava `lastTickUnixMs`; 3) `Volatile.Write(writeCount, writeCount + 1)` no mapa. Isso é release: nada gravado antes passa para depois do incremento. |
| Leitor | 1) `W1 = Volatile.Read(writeCount)` no mapa; 2) copia para um buffer próprio só amostras `i ≤ W1 − 1`, nunca a `W1`; 3) `Interlocked.MemoryBarrier()`; 4) `W2 = Volatile.Read(writeCount)`; 5) ficam só as amostras com `W2 − slotCount < i ≤ W1 − 1`. Se `W2 < slotCount`, o anel ainda não deu a volta e nada foi sobrescrito. A amostra `W2` ocupa o slot físico da amostra `W2 − slotCount`, que também é inválida. |

- **"Ao vivo"**: o painel guarda o último índice entregue e copia só o que é novo.
- **Acesso**: ponteiro obtido uma vez com `SafeMemoryMappedViewHandle.AcquirePointer`. Somar `accessor.PointerOffset`; senão a escrita cai fora do lugar quando a visão não começa no início do arquivo.
- **Flush**: se o processo morrer, as páginas ficam no cache do Windows, que as grava depois. `Flush(true)` só no encerramento. Sem flush periódico.
- **FanCurve parado**: o painel vê `lastTickUnixMs` antigo e mostra "parado há X min".

### 5.3 `data\hist\AAAA-MM[.N].fch`: agregados por minuto

- **Cabeçalho** (versão 1): `magic "FCAG"`, `version`, `channelCount`, `recordSize`, `layoutHash`, `firstMinuteUnix i64` (`−1` enquanto vazio), tabela de canais. Tamanho múltiplo de 8.
- **Registro**: `minuteUnix i64` + `sampleCount u16` + reservado até 16 bytes + por canal `(min Half, avg Half, max Half, samplesAt100 u16)`. `recordSize = 16 + 8 × channelCount`. Com 24 canais: **208 bytes**, ≈ 9,0 MB/mês, **≈ 108 MB/ano**.
- `samplesAt100` conta quantas amostras do minuto ficaram em 100%. Vale para kind 3 (%) e kind 4 (carga %). Nos outros kinds fica 0. Min, média e máx não respondem "tempo em 100%".
- A média soma em `float` e grava `Half`. `NaN` não entra em min, média, máx nem em `samplesAt100`. `sampleCount` conta a amostra mesmo assim.
- **Minuto UTC** (`unixMs / 60000 × 60000`), não "a cada 60 ciclos". Quando o minuto muda, inclusive depois de uma suspensão, o minuto anterior é gravado com o `sampleCount` que teve. No desligamento o minuto aberto também é gravado. Um `sampleCount` baixo indica minuto parcial, e amostras de antes e depois de dormir nunca se misturam.
- **Escrita**: 1 registro por minuto, `Flush(false)` (entrega ao cache do Windows, sem `flushToDisk`). `Flush(true)` no encerramento.
- **Minuto repetido**: ignorado. **Relógio para trás** (`minuteUnix` menor que o último do arquivo): abre o próximo sufixo, para a busca binária continuar ordenada dentro de cada arquivo.
- **Busca por data**: estimativa `cabeçalho + (minuto − firstMinute) / 60000 × recordSize`, corrigida com busca binária, porque minutos com o PC desligado não têm registro.
- **Queda de energia**: ao abrir, trunca o registro parcial do fim. Cabeçalho curto ou com magic errado é renomeado para `.bad` e abre um sufixo novo.
- **Retenção**: `retentionMonths` (padrão 12, o mês corrente conta), na partida e uma vez por dia.
- **Layout novo no meio do mês**: `AAAA-MM.2.fch`, depois `.3` e assim por diante.

### 5.4 `logs\events.log`

Texto, uma linha por evento, rotação em 1 MB.

### 5.5 Lacunas (PC desligado, suspensão, FanCurve parado)

- O anel guarda 24 h de **amostras**, não de tempo. Entre o slot N e o N+1 pode haver horas de intervalo.
- O gráfico `Signal` do ScottPlot assume espaçamento fixo. O painel usa `SignalXY` com o `unixMs` real.
- Intervalo **> 2 s** entre pontos vizinhos, ou um instante anterior ao anterior: a série é cortada ali, em outro `SignalXY`. Um buraco de 6 h são dois trechos. Não se preenche cada segundo faltante com `NaN`.
- O mesmo corte vale para os minutos faltantes dos `.fch`.

## 6. Pastas e permissões

Dados em `%ProgramData%\FanCurve\`, criados pelo `install-task`. O executável fica em `%ProgramFiles%\FanCurve\`, com escrita só para Administradores. O painel sobe `apply-config` por esse caminho. Um exe em pasta gravável pelo usuário, lançado elevado, seria elevação de privilégio.

| Pasta / arquivo | SYSTEM / Administradores | Usuários | Motivo |
|---|---|---|---|
| `config\` | Controle total | **Leitura** | Só um administrador muda curvas; o painel lê para exibir |
| `data\` | Controle total | **Leitura** | O painel lê; ninguém sem privilégio altera o histórico |
| `data\writer.lock` | Controle total | Leitura | A segunda instância não consegue o lock e sai |
| `logs\` | Controle total | Leitura | O painel mostra os eventos |

**Migração**: o `install-task` copia o `fancurve.json` atual para `config\` se lá não existir nenhum. O modo console (`run` na pasta do exe) continua funcionando para testes, e também respeita o `writer.lock`.

## 7. Mudanças na configuração

```jsonc
{
  // ...campos atuais (inclui reassertSeconds e resumeGapSeconds, já implementados)...
  "storage": {
    "enabled": true,
    "recentHours": 24,
    "retentionMonths": 12,
    "slowSensorsEverySec": 30   // E6
  },
  // Métricas só para registrar. As entradas e saídas das regras entram sozinhas.
  "metrics": [
    "/intelcpu/0/load/0",          // exemplos: use os IDs do "FanCurve list"
    "/intelcpu/0/power/0",
    "/gpu-nvidia/0/load/0",
    "/gpu-nvidia/0/power/0"
  ],
  "recordAllFans": true,
  "ecoMode": false              // E9
}
```

O `check` também valida `metrics`, recusa mais de 16 regras e mostra a estimativa de disco por mês (registro de `16 + 8 × canais` bytes).

## 8. Estrutura do código

```
FanCurve.sln
├─ src/FanCurve.Core/            (sem LHM; usado pelo serviço e pelo painel)
│   ├─ Config.cs, Curve.cs                         (com o serviço, fase 1)
│   ├─ Channels.cs, RecentRing.cs, HistoryStore.cs, MinuteAggregator.cs, Timeline.cs, Paths.cs   (v3)
├─ src/FanCurve/                 (serviço + CLI; Core + LHM)
│   ├─ HardwareHub.cs (E0 ✅, E1), FanController.cs, MetricsRecorder.cs (E6, E7)
│   ├─ ConfigReloader.cs, Program.cs (+ apply-config, export)
├─ src/FanCurvePanel/            (WinForms; Core + ScottPlot.WinForms 5.x)
└─ tests/FanCurve.Tests/
```

**Recarga de config** (corrigido na v2):

- **Detecção**: a cada 5 s compara a data de modificação e o tamanho do arquivo. Sem `FileSystemWatcher`.
- **Leitura**: abre com `FileShare.ReadWrite | FileShare.Delete`. Em `IOException` (o arquivo ainda está sendo gravado), tenta de novo 3× com 50 ms de intervalo. Se continuar falhando, tenta no próximo ciclo de 5 s.
- **Aplicação**: `AppConfig.Load` → `Bind` num controlador novo. Se válido, troca o controlador de forma atômica, preservando o último PWM aplicado para não dar tranco. Se inválido, mantém o atual e registra um evento. Se a lista de canais mudar, abre um anel e um `.fch` novos.
- **`apply-config` (gravação atômica)**:
  1. grava `fancurve.json.tmp`;
  2. `FileStream.Flush(flushToDisk: true)`, sem o qual uma queda de energia pode deixar o arquivo vazio;
  3. `File.Move(tmp, final, overwrite: true)`, um rename na mesma pasta, atômico no NTFS.

## 9. Painel

**Abas**

1. **Agora**: cartões com CPU/GPU, RPM e % por regra, com indicação de fail-safe; um minigráfico de 10 min; o estado do FanCurve (rodando ou parado, voltas de suspensão, próprio consumo pela E7).
2. **Histórico**:
   - período (1 h, 6 h, 24 h, 7 d, 30 d, personalizado) e seleção de canais;
   - gráficos empilhados por unidade, com eixo de tempo compartilhado e zoom sincronizado;
   - até 24 h vem do anel (resolução de 1 s); acima disso vem dos `.fch` (média, com faixa sombreada entre mínimo e máximo);
   - lacunas conforme a §5.5;
   - tabela do mesmo período: média, máximo, p95 e tempo acima do limite. Para as fans, o tempo em 100%;
   - **Salvar CSV** usa o mesmo período e os mesmos canais do gráfico.
3. **Fans**: a curva de cada regra com o ponto atual. Editável na fase 4. Os pontos se arrastam no gráfico.
4. **Configuração**: iniciar com o Windows e a lista de fans e sensores detectados neste PC.
5. **Eventos**: as últimas linhas do `events.log`.

O consumo do próprio processo (E7) fica só na linha de status. Os canais `fancurve:cpu` e `fancurve:mem` não entram nos cartões nem na lista do histórico.

A caixa **Controlar as fans** liga o `FanCurve.exe` sem prompt ao abrir a janela. Desmarcar, ou fechar a janela quando ela mesma ligou o controle, grava `stop.request` e a fan volta ao driver. Um FanCurve que já estava rodando segue depois que a janela fecha.

**Eficiência**

- O timer só roda com a janela visível.
- O "Agora" copia só os slots novos.
- Gráficos `SignalXY`, cortados quando o intervalo passa de 2 s (§5.5). O histórico carrega só o período visível.
- ReadyToRun; nenhuma referência à LHM.
- Meta: abrir em menos de 1 s com 24 h carregadas.

## 10. Fases

| Fase | Entrega | Critério de aceite |
|---|---|---|
| **0. Linha de base** | Medir o FanCurve atual (já com E0, E4 e a correção de suspensão) por 30 min, ocioso e jogando | Números anotados aqui |
| **1. Laço eficiente** | E1, E2 completo, E3, E5, divisão Core/serviço, pastas da §6, eventos | Mesmo comportamento de controle, bytes privados e CPU ≤ linha de base, testes passando |
| **2. Gravação** | `RecentRing`, `HistoryStore`, `MinuteAggregator`, `storage`/`metrics`, E6, E7, retenção, `check` com estimativa de disco | Teste de concorrência sem amostra corrompida; corte de energia simulado se recupera; custo extra < 0,1% de CPU (a medir) |
| **3. Painel (leitura)** | Abas Agora, Histórico (gráfico, tabela e CSV), Fans, Eventos | Aberto em 0,7 s neste PC, 18 MB. Minimizado, 0% de CPU em 12 s. Um dia de amostras com um buraco de 6 h copia em menos de 1 s e vira dois trechos |
| **4. Edição de curvas** | Editor na aba Fans, `apply-config` elevado e atômico, recarga a quente | Curva fora de ordem é recusada com mensagem. Válida grava o JSON e o serviço semeia o PWM anterior, subindo no máximo a rampa |
| **5. Opcionais** | E8, E9, E10, E11 | Cada um entra só se a medição mostrar ganho |

As fases 1 a 4 estão no programa: laço, gravação, painel e editor. A medição em jogo fica esperando. A fase 5 continua condicionada a uma medição que mostre ganho. A E2 segue parcial.

## 11. Testes

- **Formatos**: ida e volta (valores, NaN, Half nos limites), nome UTF-8 cortado na fronteira do caractere, slot múltiplo de 8 para qualquer número de canais, troca de layout, truncamento do registro parcial, relógio para trás abre outro sufixo.
- **Concorrência do anel**: escritor e leitor ao mesmo tempo, anel curto para forçar a volta, canal-contador nos bits do `Half` (o valor numérico de um `Half` não guarda um contador exato). Nenhuma amostra aceita pode ter contador inconsistente, nem a da cabeça nem a da borda que está sendo sobrescrita.
- **Troca de geração**: o leitor reabre quando o `recent.idx` aponta outro arquivo.
- **Agregador**: min/média/máx com NaN intercalado, `samplesAt100`, minuto parcial e salto de horas entre amostras.
- **Lacunas**: um buraco de 6 h vira dois trechos, sem um `NaN` por segundo.
- **Retenção**: apaga só os meses fora da janela.
- **Recarga de config**: arquivo travado → retry; inválida → mantém a anterior; válida → troca sem alterar o PWM no primeiro ciclo.
- **Já existentes**: curva, histerese, rampa, `Reset()` pós-suspensão, validação.

## 12. Como medir

```powershell
# Memória: o número honesto é Private Bytes (PM), não Working Set (WS)
Get-Process FanCurve | Select-Object @{n='PrivMB';e={[math]::Round($_.PrivateMemorySize64/1MB,1)}}, @{n='WSMB';e={[math]::Round($_.WorkingSet64/1MB,1)}}, CPU

# CPU% ao longo de 5 min (relativo a 1 núcleo; divida por 12 para a CPU toda no i5-12400F)
typeperf "\Process(FanCurve)\% Processor Time" -si 5 -sc 60
```

Para ver o efeito da E0, compare os bytes privados logo após a partida e depois de 24 h. Antes da E0 eles cresciam; agora devem ficar estáveis.

**Metas iniciais**, a confirmar na fase 0:
- 2º plano: < 50 MB de bytes privados e < 0,5% de um núcleo.
- Painel: < 150 MB aberto, ~0% minimizado.

**Fase 0, ocioso, neste PC (12/11/2026, 12:28–12:58).** Release em `publish\FanCurve`, curva da GPU, anel de 1 h. Amostra a cada 30 s com `PrivateMemorySize64` e o delta de `TotalProcessorTime` (percentual de um núcleo). 58 intervalos.

| | Bytes privados | CPU de um núcleo |
|---|---|---|
| Mínimo | 28,1 MB | 0,42% |
| Média | — | 0,74% |
| p95 | — | 0,99% |
| Máximo | 44,9 MB | 1,56% |
| Final | 40,4 MB | — |

A meta de memória coube (pico 44,9 MB). A de CPU não: a média ficou em 0,74% de um núcleo. Os bytes privados subiram cerca de 10 MB ao longo da meia hora (30 MB → 40 MB), com um pico de 44,9 MB no último minuto e volta a 40,4 MB. Ao encerrar, a fan da GPU voltou a 41%. O painel aberto no mesmo intervalo não entra nessa conta: os números são do processo `FanCurve`.

**Tentativa em jogo, neste PC (12/11/2026, 14:54).** O mesmo executável e a mesma curva. A amostragem e o histórico param às 15:15. O `events.log` tem o início e não tem o fim, então a fan não foi devolvida pelo encerramento normal. Nesses 21 min a GPU ficou ociosa: carga média abaixo de 1%, cerca de 2 W, temperatura caindo de 48 °C para 45 °C, fan em torno de 61%. Não conta como a metade em jogo.

| | Bytes privados | CPU de um núcleo |
|---|---|---|
| Mínimo | 27,8 MB | 0,47% |
| Média | — | 0,70% |
| p95 | — | 0,94% |
| Máximo | 37,2 MB | 1,04% |
| Final (15:15) | 37,2 MB | — |

## 13. Riscos e pontos em aberto

- **Custo real do update da LHM no seu hardware**: a E7 responde a isso na fase 2, e E8/E11 dependem disso.
- **ACL em `ProgramData`**: o `install-task` aplica as permissões explicitamente e confere.
- **Mudança de hardware**: os IDs mudam; o `check` acusa e o painel mostra os canais antigos como "históricos".
- **Relógio ajustado (NTP)**: o anel ordena por `writeCount`, não por tempo; minutos repetidos nos `.fch` são ignorados. Um salto de relógio para frente > 15 s é tratado como suspensão, o que é inofensivo: o FanCurve só reaplica o controle.
- **ScottPlot**: confirmar a versão 5.x mais recente no NuGet e travar no `.csproj`.

## 14. Revisões externas: o que entrou e o que não

| Ponto | Decisão | Motivo |
|---|---|---|
| Leitura da cabeça do anel | ✅ Aceito, corrigido | O leitor só lê até `W1 − 1`. A versão proposta mantinha um erro de uma posição na borda de trás e punha a barreira no lado errado; o protocolo final está na §5.2 |
| FileSystemWatcher: eventos múltiplos, arquivo travado, escrita não atômica | ✅ Aceito e ampliado | Trocado por polling de 5 s; retry na leitura; `.tmp` + `Flush(true)` + rename |
| Suspensão/hibernação | ✅ Aceito, **já implementado** | Confirmado no código do IT87. Além disso, a regravação periódica cobre o `WaitIsaBus(10)`, que descarta escritas em silêncio |
| Agregador após suspensão | ✅ Adaptado | Agrupar pelo minuto do relógio resolve sem descartar dados |
| Lacunas no ScottPlot `Signal` | ✅ Aceito, ajustado na v3 | O buraco fica. O `NaN` por segundo foi trocado por `SignalXY` com corte acima de 2 s (§5.5) |
| Flush do arquivo mapeado | ⚠️ Parcial | Inofensivo no encerramento, mas a justificativa estava errada: o processo morrer não perde dados |
| Afinidade de CPU na LHM | ⚠️ Diagnóstico certo, solução ajustada | Não dá para ligar só sensores de pacote pela API; a alternativa real é a E8(b) |
| Histórico interno `ValuesTimeWindow` | ✅ Aceito, **já implementado** | Confirmado: padrão de 1 dia por sensor, numa `List` com `RemoveAt(0)` |
| GPU NVIDIA lenta | ⚠️ Parcial | Lê tudo de uma vez, confirmado. O argumento de "não entra em P8" vale para notebooks com GPU híbrida, não para desktop. Virou a E11, condicionada a medição |
| WMI na inicialização | ⚠️ Parcial | Procede (`Ipmi.IsBmcPresent()`). `GC.Collect` único: aceito. `EmptyWorkingSet`: rejeitado (cosmético) |
| Ponteiro direto no arquivo mapeado | ✅ Aceito pela simplicidade | Ganho de desempenho irrelevante a 1 Hz; com o cuidado do `PointerOffset` |
| `<EnableDiagnostics>` no `.csproj` | ❌ Rejeitado | A propriedade não existe; o mecanismo real tem custo de diagnosticabilidade |
| Auto-medição sem `Process` | ✅ Aceito | `Environment.CpuUsage` (.NET 9+) e, na v3, bytes privados via `GetProcessMemoryInfo` (E7) |

## 15. Ajustes da v3

| Ponto | Decisão |
|---|---|
| Slot não múltiplo de 8 | Padding até múltiplo de 8. `writeCount` e `lastTickUnixMs` em offset múltiplo de 8 |
| Dois escritores | `data\writer.lock`. Mutex `Global\` exigiria privilégio que o modo console não tem |
| Troca do arquivo mapeado | `recent.idx` aponta `recent-<N>.bin`. O binário vigente não é renomeado; o leitor reabre quando a geração muda |
| `Volatile` | No `writeCount` do cabeçalho mapeado, com `PointerOffset` somado ao ponteiro |
| Tempo em 100% | Campo `samplesAt100` em cada canal do `.fch` |
| Bytes privados | `GetProcessMemoryInfo` / `PrivateUsage` no processo atual |
| Média e relógio para trás | Soma do minuto em `float`. `minuteUnix` menor que o último abre outro sufixo |
| Buraco no gráfico | `SignalXY` e corte da série acima de 2 s |
