# CHANGELOG-AGENT

Diário de mudanças feitas com o agente (intenção + resolução).
Mais recente no topo.

## 2026-11-17 — gráficos parados e setas no tempo

- **Chat:** remover deslocar e mexer os gráficos; layout mais moderno, mantendo a simplicidade
- **Intenção:** o mouse não arrasta o gráfico; o período anda por setas
- **Resolução:**
  - Arrastar, zoom e rolagem saíram dos gráficos
  - No histórico, ← e → mudam o intervalo, sem passar do agora
  - Botões e tabelas ficaram chapados, com a mesma grade clara
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/ChartDraw.cs`, `src/FanCurvePanel/ChannelRangeBar.cs`, `src/FanCurvePanel/Theme.cs`

## 2026-11-17 — textos que estavam cortados

- **Chat:** análise geral de UI e UX, tem coisas que estão cortando
- **Intenção:** ler a barra de baixo, as datas dos gráficos e o caminho da configuração por inteiro
- **Resolução:**
  - A linha de status ganhou margem, então "Rodando" não perde a primeira letra
  - Os gráficos reservam espaço para a unidade e para a data
  - O caminho do arquivo fica ao lado do checkbox e encurta com reticências
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/MainForm.cs`, `src/FanCurvePanel/ConfigPage.cs`, `src/FanCurvePanel/CurveEditor.cs`

## 2026-11-17 — tela parou de piscar e gráficos mais limpos

- **Chat:** com essa última versão a tela fica piscando; gráficos mais modernos, mas simples
- **Intenção:** a janela estável e os gráficos sem a grade pesada
- **Resolução:**
  - O histórico deixou de ser recriado a cada segundo
  - Os gráficos ao vivo só redesenham na aba Agora
  - Linha azul, grade clara e legenda sem sombra
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/MainForm.cs`, `src/FanCurvePanel/ChartDraw.cs`

## 2026-11-17 — gráfico de potência visível no histórico

- **Chat:** o package não mostra nos gráficos?
- **Intenção:** ver o GPU Package em watts junto com temperatura e percentual
- **Resolução:**
  - Os gráficos dividem a altura da aba, um por unidade
  - Potência em W deixa de ficar escondida atrás da tabela
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/MainForm.cs`

## 2026-11-17 — Configuração distingue temperatura, carga e potência

- **Chat:** em Config isso não deveria ficar claro?
- **Intenção:** ver que GPU Core da temperatura não é o GPU Core da carga, nem o GPU Package
- **Resolução:**
  - A lista curta mostra a carga e a potência que o histórico grava, sem a lista inteira do processador
  - Nome repetido ganha o tipo: GPU Core · temperatura e GPU Core · carga
  - O resumo cita a temperatura, a carga e a potência pelo nome
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/SensorInventory.cs`, `src/FanCurvePanel/ConfigPage.cs`

## 2026-11-17 — histórico com a tabela, sem o custo do processo

- **Chat:** as abas histórico e estatísticas não dá para integrar em uma só? FanCurve CPU e memória já não tínhamos removido?
- **Intenção:** um só lugar para o período, e tirar da lista o consumo do próprio programa
- **Resolução:**
  - A aba Estatísticas saiu; a tabela de média, máximo, p95 e tempo acima do limite ficou embaixo do gráfico
  - O limite "Acima de" e o CSV usam os mesmos canais e o mesmo período
  - FanCurve CPU e FanCurve memória saíram dos cartões e da lista; continuam na linha de status
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/MainForm.cs`

## 2026-11-16 — pontos arrastáveis na curva

- **Chat:** seria interessante que desse para mover os pontos clicando nos gráficos
- **Intenção:** ajustar a curva direto no gráfico da aba Fans
- **Resolução:**
  - O clique esquerdo arrasta o ponto; a tabela acompanha na hora
  - A temperatura não cruza o ponto vizinho e o percentual fica entre 0 e 100
  - A curva só vai para o arquivo em Aplicar
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/CurveEditor.cs`. Arraste do último ponto, de 80 °C / 100% para 77 °C / 65%, visível na tabela e na linha

## 2026-11-16 — layout mais justo

- **Chat:** o layout geral poderia modernizar e otimizar melhor o uso do espaço, veja a parte de Fans
- **Intenção:** a curva e as outras abas ocuparem a janela em vez de deixarem faixa vazia
- **Resolução:**
  - A curva da fan ocupa a altura da aba; adicionar e remover ficam na mesma linha
  - Os cartões de Agora ficaram mais baixos e os gráficos ganharam o espaço
  - Histórico e Configuração perderam a coluna e o bloco altos que sobravam
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/CurveEditor.cs`, `src/FanCurvePanel/MainForm.cs`

## 2026-11-16 — CSV no Histórico

- **Chat:** por UI e UX não seria interessante que fossem no mesmo lugar?
- **Intenção:** exportar o CSV no mesmo lugar em que se vê o histórico
- **Resolução:**
  - A aba Exportar saiu
  - Histórico ganhou o botão Salvar CSV, com o período e os canais já marcados
  - A mensagem de arquivo gravado aparece na mesma aba
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/MainForm.cs`

## 2026-11-16 — lista curta de sensores

- **Chat:** ficou com muita coisa, 64 temperaturas em 167
- **Intenção:** ver só o que dá para usar no controle da fan
- **Resolução:**
  - A aba Configuração mostra fans que aceitam controle e temperaturas com leitura
  - Sensores vazios ficam atrás de "Mostrar sensores sem leitura"
  - O texto de cima resume em uma frase o que dá para controlar
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/SensorInventory.cs`

## 2026-11-16 — aba Configuração

- **Chat:** precisamos trabalhar na UI para deixar mais claro
- **Intenção:** um painel para iniciar com o Windows e ver fans e sensores detectados
- **Resolução:**
  - A aba Configuração tem a opção de iniciar com o Windows e a lista do hardware, com tipo, nome, valor e identificador
  - A barra de cima ficou só com Controlar as fans
  - O gráfico de Agora mostra os últimos 10 minutos, e o duplo clique deixa de abrir o medidor de render
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/ConfigPage.cs`, `src/FanCurvePanel/SensorInventory.cs`

## 2026-11-16 — duplo clique acha a configuração local

- **Chat:** quando clico no exe não está abrindo nem acontecendo nada
- **Intenção:** abrir o painel com dois cliques e ver o controle da fan
- **Resolução:**
  - Sem argumentos, a janela ia para `C:\ProgramData\FanCurve`, que está vazio
  - O duplo clique a partir da pasta de build agora usa `local\fancurve.json`, `local\data` e `local\logs`
  - Com essa configuração, a caixa Controlar as fans liga o FanCurve
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/PanelPaths.cs`

## 2026-11-16 — painel liga o controle da fan

- **Chat:** não tem como nesse painel ligar o controle de fan?
- **Intenção:** ligar a fan pela janela, sem prompt, ao abrir o painel
- **Resolução:**
  - A caixa Controlar as fans fica marcada ao abrir e sobe o FanCurve.exe escondido
  - Desmarcar, ou fechar a janela, pede a parada e a fan volta ao driver
  - Um FanCurve que já estava rodando não é encerrado pelo fechamento da janela
- **Resultado:** ok
- **Evidência:** `src/FanCurvePanel/FanSession.cs`
- **Pendências:** medição em jogo, E2 completa e a fase 5

## 2026-11-16 — sensor externo fora do plano

- **Chat:** não quero sensor externo, remova isso do plano
- **Intenção:** tirar o ESP32 da fase 5
- **Resolução:**
  - A fase 5 fica só com E8, E9, E10 e E11
  - O sensor externo saiu da tabela de fases
  - Não havia código desse sensor para apagar
- **Resultado:** ok
- **Evidência:** `IMPLEMENTACAO.md` §10

## 2026-11-16 — medição em jogo fica esperando

- **Chat:** tem como fazer outras partes e deixar essa esperando?
- **Intenção:** seguir o plano sem a meia hora com jogo
- **Resolução:**
  - A medição em jogo permanece pendente
  - As fases 1 a 4 já estão no programa; a tabela do plano marca E1, E3, E5, E6 e E7 como feitas
  - A fase 5 continua esperando uma medição que mostre ganho
- **Resultado:** ok
- **Evidência:** `IMPLEMENTACAO.md` §4 e §10
- **Pendências:** medição em jogo, E2 completa e a fase 5

## 2026-11-16 — medição em jogo interrompida

- **Chat:** Finalizou? deu certo?
- **Intenção:** saber se a meia hora com jogo fechou
- **Resolução:**
  - O `run` começou às 14:54 e a amostragem parou às 15:15, sem a linha de fim no log
  - A GPU ficou ociosa: carga média abaixo de 1% e cerca de 2 W, temperatura caindo de 48 °C para 45 °C
  - CPU média 0,70% de um núcleo; bytes privados de 27,8 a 37,2 MB

- **Resultado:** parcial
- **Evidência:** `local/measure-game.csv`, `local/logs/events.log`, `local/data/hist/2026-11.fch`
- **Pendências:** repetir os 30 min com um jogo aberto

## 2026-11-12 — fase 4, editor de curva

- **Chat:** pode fazer a parte 4 então?
- **Intenção:** editar a curva na aba Fans e gravar sem reiniciar o FanCurve
- **Resolução:** a aba Fans edita os pontos, recusa curva inválida e grava o JSON de forma atômica. Se a pasta exigir administrador, sobe o `apply-config`. O serviço que já está rodando semeia o PWM anterior e só anda na rampa
- **Resultado:** ok
- **Evidência:** testes de curva fora de ordem, `AtomicWriteRoundTripsTheCurve` e `SeedKeepsTheOutputWhenTheNextCurveWantsMore`
- **Pendências:** medição em jogo e a fase 5

## 2026-11-12 — fase 3, painel de leitura

- **Chat:** pode fazer a parte 3 então?
- **Intenção:** fechar o painel só de leitura no critério da fase 3
- **Resolução:** o timer para com a janela minimizada; o gráfico ao vivo só redesenha quando chega amostra nova; Histórico e Estatísticas carregam ao abrir a aba. Abriu em 0,7 s com 18 MB. Minimizado, 0% de CPU. 24 h com um buraco de 6 h copiam em menos de 1 s e ficam em dois trechos
- **Resultado:** ok
- **Evidência:** teste `DayOfSamplesCopiesInUnderASecondAndKeepsTheHole`
- **Pendências:** edição da curva (fase 4) e a medição em jogo

## 2026-11-12 — opção de iniciar com o Windows

- **Chat:** queria que isso fosse uma opção de setar em algum lugar
- **Intenção:** ligar e desligar a inicialização automática no painel, sem abrir o Agendador de Tarefas
- **Resolução:** caixa "Iniciar com o Windows" no painel. Marcar pede administrador e cria a tarefa. Desmarcar apaga a tarefa e deixa um FanCurve que já esteja rodando seguir até o desligamento
- **Resultado:** ok
- **Evidência:** `FanCurve startup status|on|off`
- **Pendências:** a caixa ainda não foi marcada neste PC

## 2026-11-12 — medição ociosa de 30 min

- **Chat:** sim, por favor
- **Intenção:** ter a linha de base do FanCurve parado, antes de instalar a tarefa
- **Resolução:** `run` de 12:28 a 12:58 na curva da GPU. Bytes privados 28,1–44,9 MB (fim 40,4). CPU média 0,74% de um núcleo, p95 0,99%. A fan voltou a 41%
- **Resultado:** ok
- **Evidência:** `local/measure.csv`, `local/logs/events.log` (início 12:28:57, fim 12:58:57)
- **Pendências:** medição em jogo e `install-task`

## 2026-11-12 — painel de leitura

- **Chat:** sim, por favor
- **Intenção:** medir o processo por 30 min, mostrar os gráficos e deixar o FanCurve subir com o Windows
- **Resolução:**
  - `FanCurvePanel.exe` lê o anel e o histórico, sem a LibreHardwareMonitor. Abas Agora, Histórico, Estatísticas, Fans, Eventos e Exportar
  - A medição ociosa da GPU ficou rodando; os números finais entram quando os 30 min fecham
- **Resultado:** parcial
- **Evidência:** painel aberto em `local\data`, 26 testes do Core
- **Pendências:** anotar a medição e o `install-task`

## 2026-11-12 — curva da GPU neste PC

- **Chat:** pode fazer isso?
- **Intenção:** pôr uma fan deste computador para seguir uma curva
- **Resolução:**
  - O PawnIO já estava instalado e em execução; temperaturas da CPU continuaram vazias e o controlador da placa-mãe não apareceu
  - Configuração real em `local/fancurve.json` para a GPU (`/gpu-nvidia/0/temperature/0` → `/gpu-nvidia/0/control/0`)
  - `run` de 15 s: 47 °C pediu 62%. Ao sair, a fan voltou para 41%
- **Resultado:** ok
- **Evidência:** `local/logs/events.log`, comando `status` (15 amostras, fail-safe 0)
- **Pendências:** medição de 30 min, `install-task` e o painel

## 2026-11-12 — FanCurve.exe, o controle das fans

- **Chat:** desenvolver o FanCurve
- **Intenção:** ter o programa que lê sensores e aplica a curva, porque o executável não existia neste PC
- **Resolução:**
  - `FanCurve.exe` com `list`, `check`, `run`, `apply-config` e `install-task`
  - Curva, histerese, piso, rampa, fail-safe e volta de suspensão em `RuleRuntime`, com testes
  - Laço de 1 s na LibreHardwareMonitor 0.9.6, regravação do PWM e gravação no anel e no histórico já existentes
- **Resultado:** parcial
- **Evidência:** `src/FanCurve`, `samples/fancurve.example.json`, 26 testes
- **Pendências:** painel, `install-task` neste PC (pede administrador) e a medição da fase 0 com o `run` aberto

## 2026-11-12 — plano v3 e Core de armazenamento

- **Chat:** ajustar plano e iniciar
- **Intenção:** incorporar os ajustes do anel e do histórico e começar a implementação
- **Resolução:**
  - O plano passou para a v3: slot alinhado a 8 bytes, `writer.lock`, `recent.idx` + `recent-<N>.bin`, `Volatile` no mapa, `samplesAt100`, bytes privados, média em `float`, relógio para trás e corte de lacuna com `SignalXY`
  - `FanCurve.Core` grava e lê o anel, o histórico mensal e o agregador por minuto, sem a LibreHardwareMonitor
  - 18 testes passaram, inclusive o escritor e o leitor ao mesmo tempo por 10 s com volta do anel
- **Resultado:** parcial
- **Evidência:** `src/FanCurve.Core`, `tests/FanCurve.Tests`, `IMPLEMENTACAO.md`
- **Pendências:** ligar o Core ao laço do serviço (o código da §0 não está nesta pasta), painel, medição da fase 0, E6, E7 no processo e o `check`
