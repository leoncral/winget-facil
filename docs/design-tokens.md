# Asa — Design Tokens e Especificação Visual (v1 — DECIDIDO)

> **Fonte de verdade para toda implementação visual do Asa.**
> Derivado de `visual-update/DESIGN.md` ("Cybernetic Mica") + os mockups `visual-update/image*.png`.
> Conflitos internos do DESIGN.md foram resolvidos aqui e registrados na seção 1.
> Este documento substitui a tarefa V0-2/V0-7 do `plano.md` — tokens e contraste já estão fechados.

---

## 1. Decisões fechadas

| # | Decisão | Resolvido como |
|---|---|---|
| D1 | Tema claro | **Dark-only na v1.** Nenhum seletor de tema. Suporte a **Alto Contraste** (forced colors) é obrigatório via `ThemeDictionaries`. Tema claro vai para o backlog. |
| D2 | Shell canônico | O das imagens `image.png` / `image copy.png`: **title bar de 48 px com pill "Asa" + badge "WINGET FACIL"** e sidebar de 240 px. O shell com logotipo dentro da sidebar (`image copy 2.png`) é **descartado**. A faixa "Repositório Oficial Winget + Microsoft Store Ativos" é **descartada** (a informação vai para a barra de status). |
| D3 | Barra de status | **Global** (todas as páginas), 32 px, na base da janela. |
| D4 | Origem do pacote | `msstore` se a origem for msstore; `local` se o ID começar com `ARP\` ou `MSIX\` ou não houver origem; caso contrário `winget`. |
| D5 | Categorias | **Catálogo curado embutido no app** (`Assets/catalog.json`), com as 4 categorias do mockup (Desenvolvimento, Utilitários, Multimídia, Jogos & Launchers) e ~30–60 IDs conhecidos cada. Se um ID não estiver no catálogo, ele simplesmente não aparece em categoria — **nunca** classificar por adivinhação. |
| D6 | "Críticos" | **Major upgrade** (primeiro componente de versão maior que a instalada) **ou** presente na lista curada `security` do catálogo. Tooltip explica o motivo ("Atualização de versão principal" / "Atualização de segurança"). |
| D7 | Tamanho estimado / espaço | Obtido sob demanda via `winget show --id <id> --exact` em lote, com cache em `%LOCALAPPDATA%\Asa\metadata-cache.json` (TTL 24 h), cancelável. **Se indisponível, o número é omitido** — nunca exibir 0 ou valor inventado. "Total estimado" no cabeçalho de Atualizações = soma dos downloads estimados dos itens filtrados. |
| D8 | Painel de hash | **Informativo.** O winget já valida SHA-256; o rodapé declara a garantia e reflete o estado real da preferência de instalação silenciosa. **Não** há exibição de hash por pacote. |
| D9 | Telas sem mockup | Histórico e Configurações especificados na seção 9 deste documento. |
| D10 | Fonte de verdade das cores | **Tokens M3 do YAML** para superfícies e texto; a seção "Colors" do DESIGN.md define os **papéis de sinal** (ciano/esmeralda/magenta). `primary` do M3 (quase branco) **não** é usado como cor de botão. |
| D11 | Fontes | Embutir **Space Grotesk** (600/700), **Plus Jakarta Sans** (400/500/600) e **JetBrains Mono** (500/600) em `Assets/Fonts`, com os textos de licença OFL. |
| D12 | Glow | v1 **sem dependência nova**: glow é feito com **camadas XAML** (`Border` com `RadialGradientBrush` sobreposto, opaco→transparente). `CompositionDropShadow` só entra se o spike R-0 provar que as camadas não bastam. **Glow proibido em linhas de lista.** |
| D13 | Conflito spec × mockup | **Tokens, densidade e métricas seguem esta spec**; **layout de página segue os mockups**. |
| D14 | Acrylic | v1 usa **Mica (`MicaKind.BaseAlt`) no fundo da janela + superfícies sólidas em camadas**. Nada de acrylic aninhado (custo alto, ganho visual baixo sobre Mica). O spike R-0 testa acrylic apenas na sidebar e adota só se for gratuito em CPU. |

---

## 2. Cores

### 2.1 Superfícies

| Token | Hex | Uso |
|---|---|---|
| `AsaSurfaceCanvas` | `#08090d` | Substrato da janela (Z0), atrás de tudo |
| `AsaSurfaceBase` | `#111318` | Fundo base das páginas |
| `AsaSurfaceSunken` | `#0c0e12` | Poço: container de tabela, faixas rebaixadas |
| `AsaSurfaceRail` | `#1a1c20` | Sidebar, title bar |
| `AsaSurfaceCard` | `#1e2024` | Cards nível 1 |
| `AsaSurfaceHover` | `#282a2e` | Hover de linha/card |
| `AsaSurfaceActive` | `#333539` | Linha selecionada, item ativo |
| `AsaOutline` | `#849495` | Borda de controle em destaque |
| `AsaOutlineVariant` | `#3b494b` | Divisores |

### 2.2 Texto

| Token | Hex | Contraste em `#111318` | Uso |
|---|---|---|---|
| `AsaTextPrimary` | `#e2e2e8` | **14,4:1** | Títulos, nomes |
| `AsaTextSecondary` | `#b9cacb` | **11,0:1** | Descrições, legendas |
| `AsaTextTertiary` | `#94a3b8` | **7,3:1** | IDs, metadados, badges neutros |
| `AsaTextDisabled` | `#64748b` | 3,9:1 | **Exclusivo de controles desabilitados.** Nunca usar para informação |

> ⚠️ `AsaTextTertiary` **não** pode ser usado sobre `AsaSurfaceActive` (`#333539`): o contraste cai para ~2,6:1. Sobre linhas em hover/seleção use `AsaTextSecondary` (~7,2:1).

### 2.3 Sinais

| Papel | Tokens |
|---|---|
| **Ação** (marca, CTA, foco) | `AsaAction` `#00f0ff` · `AsaActionHover` `#7df4ff` · `AsaActionPressed` `#00dbe9` · `AsaOnAction` `#002022` (12,1:1 sobre `AsaAction`) |
| **Atualização / sucesso** | `AsaUpdate` `#00e575` (11,0:1) · `AsaUpdateBright` `#62ff96` · `AsaOnUpdate` `#00210b` (10,2:1 sobre `AsaUpdate`) |
| **Crítico / terciário** | `AsaCritical` `#d946ef` (5,4:1) · `AsaCriticalContainer` `#ffc7ff` · `AsaOnCritical` `#36003e` |
| **Erro** | `AsaError` `#ffb4ab` · `AsaErrorContainer` `#93000a` · `AsaOnError` `#690005` · `AsaOnErrorContainer` `#ffdad6` |

### 2.4 Bordas e translúcidos

| Token | Valor | Uso |
|---|---|---|
| `AsaBorderGhost` | `rgba(255,255,255,0.08)` | Divisores decorativos, contorno de superfície |
| `AsaBorderControl` | `rgba(255,255,255,0.16)` | Borda de controle em repouso |
| `AsaBorderHover` | `rgba(0,240,255,0.50)` | Borda de controle em hover — **4,0:1** ✅ |
| `AsaFocusRing` | `#00f0ff` sólido, 2 px | Anel de foco de teclado — **13:1** ✅ |
| `AsaScrimCard` | `rgba(24,32,44,0.75)` | Preenchimento de card (Z2) |

> **Decisão consciente de acessibilidade:** as bordas `AsaBorderGhost`/`AsaBorderControl` são decorativas e ficam abaixo de 3:1. A identificação de controles interativos é garantida por (a) preenchimento distinto do entorno, (b) anel de foco `AsaFocusRing` de 2 px (13:1) e (c) ícones/rótulos com ≥4,5:1. Nenhum estado (hover, ativo, selecionado, erro) pode depender **apenas** da borda.

### 2.5 Nomenclatura de recursos XAML

- Cores: `Asa<Área><Papel>Color` → ex.: `AsaActionColor`, `AsaSurfaceCardColor`.
- Brushes: `Asa<Área><Papel>Brush` → ex.: `AsaActionBrush`, `AsaTextTertiaryBrush`.
- Tipografia: `Asa<Escala>TextStyle`.
- Raios: `AsaRadiusSm`, `AsaRadiusDefault`, `AsaRadiusMd`, `AsaRadiusLg`, `AsaRadiusXl`, `AsaRadiusFull` (tipo `CornerRadius`).
- Espaçamento: `AsaSpaceXs/Sm/Md/Lg/Xl` (`x:Double`), `AsaGutter`, `AsaMarginPage`, `AsaMarginWindow` (`Thickness`).
- Todos os recursos vivem em `Styles/Tokens.xaml`, com `ResourceDictionary.ThemeDictionaries` para `Dark` e `HighContrast`.

---

## 3. Tipografia

Famílias embutidas em `Assets/Fonts` (pesos necessários apenas): `SpaceGrotesk` 600/700, `PlusJakartaSans` 400/500/600, `JetBrainsMono` 500/600. Licenças OFL em `Assets/Fonts/LICENSE-*.txt`.

| Estilo | Família | Tamanho / linha | Peso | CharacterSpacing | Uso |
|---|---|---|---|---|---|
| `AsaDisplayTextStyle` | Space Grotesk | 36 / 44 | 700 | −30 | Headline do hero (Descobrir) |
| `AsaHeadlineLgTextStyle` | Space Grotesk | 28 / 36 | 600 | −20 | Título de página |
| `AsaHeadlineMdTextStyle` | Space Grotesk | 20 / 28 | 600 | −10 | Título de seção/card |
| `AsaBodyLgTextStyle` | Plus Jakarta Sans | 15 / 22 | 400 | 0 | Subtítulo de página, corpo destacado |
| `AsaBodyMdTextStyle` | Plus Jakarta Sans | 13 / 18 | 400 | 0 | Corpo padrão, nomes de pacote |
| `AsaBodySmTextStyle` | Plus Jakarta Sans | 12 / 16 | 400 | 0 | Legendas, resumos, texto da barra de status |
| `AsaLabelCodeTextStyle` | JetBrains Mono | 12 / 16 | 500 | 0 | Versões, IDs, atalhos (`Ctrl+F`) |
| `AsaLabelBadgeTextStyle` | JetBrains Mono | 11 / 14 | 600 | +40 | Badges (uppercase) |

**Exceção justificada:** `AsaLabelBadgeTextStyle` fica em 11 px, abaixo do piso de 12 px — aceitável por ser monoespaçada, 600 e uppercase, e por exigir contraste ≥ 7:1 (garantido pelo `AsaTextTertiary`/variantes). Todos os demais estilos respeitam o piso de 12 px.

Regras: versões e IDs sempre em `AsaLabelCodeTextStyle` ou `AsaLabelBadgeTextStyle`; nunca aplicar `AsaTextDisabled` a texto informativo.

---

## 4. Espaçamento, raios e métricas de layout

| Token | Valor |
|---|---|
| `AsaSpaceXs` | 4 |
| `AsaSpaceSm` | 8 |
| `AsaSpaceMd` | 12 |
| `AsaSpaceLg` | 20 |
| `AsaSpaceXl` | 32 |
| `AsaGutter` | 16 |
| `AsaGutterCompact` | 8 |
| `AsaMarginPage` | 28 (horizontal e vertical das páginas) |
| `AsaMarginWindow` | 16 (margem segura dos controles de janela) |

| Raio | Valor | Uso |
|---|---|---|
| `AsaRadiusSm` | 4 | Badges de origem, tags pequenas |
| `AsaRadiusDefault` | 8 | Inputs, botões, linhas de lista |
| `AsaRadiusMd` | 12 | Cards, painéis |
| `AsaRadiusLg` | 16 | Cards grandes, hero interno |
| `AsaRadiusXl` | 24 | Bloco hero, painéis elevados |
| `AsaRadiusFull` | 999 | Pills, contadores, avatares circulares |
| Avatar | 40×40, raio **10** | Ícone de pacote com inicial |

Métricas fixas: **title bar 48 px**, **sidebar 240 px** (colapsada 64 px), **linha de lista 56 px** (altura mínima), **barra de status 32 px**, **barra de atividade 56 px**.

**Janela:** mínimo **900 × 600**. Abaixo de **1040 px** de largura: a sidebar colapsa para 64 px e as colunas secundárias da tabela (ORIGEM) são ocultadas. Nunca permitir clipping de ações — a coluna de ação é sempre visível.

---

## 5. Elevação, profundidade e glow

- **Z0 `AsaSurfaceCanvas`** com bloom radial ciano fixo: `RadialGradientBrush` centralizado no topo, `rgba(0,240,255,0.04)` → transparente, raio ~70% da largura. Estático, sem custo de animação.
- **Z1 (sidebar, title bar, container de tabela):** superfície sólida (`AsaSurfaceRail` / `AsaSurfaceSunken`) + borda 1 px `AsaBorderGhost`.
- **Z2 (cards, linhas em destaque):** preenchimento `AsaScrimCard` sobre a superfície + borda interna com `LinearGradientBrush(180°)` de `rgba(255,255,255,0.10)` → `rgba(255,255,255,0.02)`.
- **Glow (v1, sem dependência):** um `Border` irmão, atrás do elemento, preenchido com `RadialGradientBrush` na cor do sinal (ciano/esmeralda/magenta) de alfa 0.28 → 0, com margens negativas de ~8 px. Usar **no máximo 3 camadas** e somente em: hero, botão primário, item ativo da sidebar, badge de contador com novidade.
- **Proibido:** glow por linha de lista, glow em elementos que rolam, glow durante animação de lista.
- **Animações desativadas ou Alto Contraste ativo:** glow, pulse e blur são removidos (trocar por borda sólida `AsaAction`).
- **Pulse** do contador de atualizações: `Storyboard` de 1,6 s em opacidade 1 → 0.75 → 1, no máximo 3 ciclos ao detectar novidades; respeitar `UISettings.AnimationsEnabled`.

---

## 6. Componentes e estados

### 6.1 Botões
| Variante | Repouso | Hover | Pressionado | Desabilitado |
|---|---|---|---|---|
| **Primário** (Neon) | fundo `AsaAction`, texto `AsaOnAction`, `AsaLabelBadgeTextStyle` uppercase, raio 8, glow ciano | fundo `AsaActionHover`, brilho 110% | fundo `AsaActionPressed` | fundo `AsaSurfaceActive`, texto `AsaTextDisabled`, sem glow |
| **Ghost** | fundo `rgba(255,255,255,0.05)`, borda 1 px `AsaBorderControl`, texto `AsaTextPrimary` | borda `AsaBorderHover`, texto branco | fundo `AsaSurfaceHover` | texto `AsaTextDisabled`, sem borda |
| **Ícone** (refresh, fechar) | sem fundo, ícone `AsaTextSecondary`, alvo 40×40 | fundo `AsaSurfaceHover`, ícone `AsaAction` | fundo `AsaSurfaceActive` | ícone `AsaTextDisabled` |

Todo botão tem `MinHeight`/`MinWidth` de 40 px (hit-test) e anel de foco de 2 px `AsaFocusRing` quando focado por teclado.

### 6.2 Campo de busca
Fundo `#121822` (opaco), borda 1 px `AsaBorderControl`, raio 8, altura 44. **Foco:** borda `AsaAction` + sombra de anel `rgba(0,240,255,0.15)` de 3 px (implementada como `Border` externo). Placeholder em `AsaTextTertiary`. Hint de atalho em `AsaLabelCodeTextStyle` + `AsaTextDisabled` só para o atalho.

### 6.3 Linha de lista (56 px)
- Fundo em repouso: transparente (sobre `AsaSurfaceSunken`), divisores de 1 px `AsaBorderGhost`.
- Hover: fundo `AsaSurfaceHover` + hairline de 2 px `AsaAction` na borda esquerda.
- Selecionada: fundo `AsaSurfaceActive`; checkbox marcado.
- Colunas: checkbox 28 → avatar 40 → nome/ID (flexível, `MinWidth` 220) → origem 96 → versão 110 → disponível/ação 120.
- Versão instalada em `AsaLabelCodeTextStyle`/`AsaTextTertiary`; **nova versão em `AsaLabelCodeTextStyle`/`AsaUpdate`**; "Em dia" com ícone ✓ `AsaUpdate`.
- Estado **queued/running**: substitui o botão de ação por chip (`AsaBodySmTextStyle`) + `ProgressRing` de 14 px; altura da linha **não** muda.
- Truncamento: `TextTrimming="CharacterEllipsis"` + tooltip com o valor completo.

### 6.4 Chips e filtros
Capsule raio `AsaRadiusFull`, fundo `rgba(255,255,255,0.04)`, borda 1 px `AsaBorderGhost`, texto `AsaBodySmTextStyle`/`AsaTextSecondary`, altura 32 com área clicável de 40. Hover: texto branco + borda `AsaBorderHover`. Ativo: fundo `AsaSurfaceActive`, borda `AsaAction`, texto `AsaTextPrimary`, contador em `AsaUpdate`. Chips **sempre** com wrap; nunca rolagem horizontal.

### 6.5 Checkbox
18 × 18, raio 4, borda 1 px `rgba(255,255,255,0.25)`. Marcado: fundo `AsaAction`, check preto, glow ciano de 4 px. Hit-test 40 × 40. Foco: anel `AsaFocusRing`. Estado indeterminado (select-all parcial) com barra `AsaAction`.

### 6.6 Badges
- **Origem/repositório** (`winget`, `msstore`, `local`): `AsaLabelBadgeTextStyle` uppercase, fundo `rgba(255,255,255,0.08)`, texto `AsaTextTertiary`, raio 4, altura 22. `msstore` recebe tom magenta-baixo (`AsaCriticalContainer` a 20% de opacidade) e `local` fica neutro.
- **Contador de atualizações** (sidebar): pill `AsaUpdate`, texto `AsaOnUpdate`, `AsaLabelBadgeTextStyle`.
- **Crítico**: chip com `AsaCritical` a 18% de fundo, texto `AsaCriticalContainer`, tooltip com o motivo.
- **Modo de instalação** (rodapé de Atualizações): "Silencioso" quando ativo, "Interativo" caso contrário — sempre o estado real.

### 6.7 Estados de página
- **Vazio**: ícone 40 px `AsaTextDisponível`→`AsaTextTertiary`, título `AsaHeadlineMdTextStyle`, descrição `AsaBodyMdTextStyle`/`AsaTextSecondary`, ação primária opcional. Sempre contextual (vazio real ≠ vazio de filtro).
- **Carregando inicial**: 6 linhas skeleton de 56 px, blocos com raio 8 e `AsaSurfaceHover` em opacidade 0.4 → 0.7 (animação desativada se `AnimationsEnabled == false`).
- **Recarregando**: lista permanece + `ProgressRing` de 16 px no cabeçalho.
- **Erro**: `InfoBar` acima da lista, severidade `Error`, com botão "Tentar novamente"; **a lista não é escondida**. Mensagens de código do winget traduzidas para ação (seção 8).
- **Sucesso**: barra de atividade em `AsaUpdate` com ✓; auto-hide em 7 s; o log permanece no Histórico.

### 6.8 Barra de atividade (global, 56 px)
Ícone de estado (`AsaAction` = em andamento, `AsaUpdate` = sucesso, `AsaCritical` = cancelado/atenção, `AsaError` = falha) · título `AsaBodyMdTextStyle` 600 · detalhe `AsaBodySmTextStyle` (com ellipsis + tooltip) · progresso (determinado quando houver % — seção 8) · botão "Cancelar" (só enquanto `IsBusy`) · botão "Registrar/Log" · botão fechar. `AutomationProperties.LiveSetting="Polite"`.

### 6.9 Barra de status (global, 32 px)
Fundo `AsaSurfaceRail`, `AsaBodySmTextStyle`. Esquerda: `● Repositório WinGet conectado · Catálogo atualizado há {X} min` (ponto em `AsaUpdate`/`AsaCritical`/`AsaError` conforme saúde). Direita: `{N} selecionado(s) · {M} atualizações pendentes`, mais `· Download estimado {soma} GB` quando houver seleção e metadados disponíveis.

---

## 7. Tradução CSS → WinUI 3

| DESIGN.md (CSS) | Implementação WinUI 3 |
|---|---|
| `box-shadow` multinível (glow) | Camadas `Border` + `RadialGradientBrush` (v1). Fallback: `CompositionDropShadow` via `ElementCompositionPreview` |
| `backdrop-filter: blur(24px) saturate(180%)` | `MicaBackdrop` (`MicaKind.BaseAlt`) + superfícies sólidas. Acrylic só na sidebar, se aprovado no R-0 |
| `border-radius` | `CornerRadius` com os tokens da seção 4 |
| `letter-spacing: -0.03em` | `CharacterSpacing = -30` (unidade 1/1000 em) |
| `line-height` | `LineHeight` no `TextBlock`/`Style` |
| `font-family` customizada | `FontFamily="ms-appx:///Assets/Fonts/<arquivo>.ttf#<Nome>"` |
| `rgba(...)` | `SolidColorBrush` com `Color` + `Opacity`, ou `LinearGradientBrush`/`RadialGradientBrush` |
| `transition` / `pulse` | `Storyboard`/`Composition` no visual states; curto (120–200 ms) e respeitando `AnimationsEnabled` |
| `:hover`, `:focus`, `:active` | `VisualStateManager` com os estados Hover/Focus/Selected/Disabled |
| Custom checkbox | `ControlTemplate` novo, mantendo `MinHeight/MinWidth` de 40 px |
| Grade 12 colunas | `Grid` com colunas `*`/`Auto` e `MinWidth` nas colunas de texto |

---

## 8. Regras de honestidade dos dados

1. **Tamanho/estimativa:** só exibir quando o dado existir; caso contrário, omitir o número (nunca `0 B`, nunca "~0 GB").
2. **Origem:** sempre derivada pela regra D4; sem origem desconhecida exibida como `winget`.
3. **Categoria:** só do catálogo curado (D5). Nenhum pacote é classificado por heurística de nome.
4. **Crítico:** só por major upgrade ou lista `security` (D6), com tooltip explicando.
5. **Hash:** declaração informativa, nunca verificação simulada (D8).
6. **Erros do winget:** traduzir códigos conhecidos para ação do usuário, com o detalhe bruto disponível no log/Histórico. Exemplos: `0x8a15000b` → "O instalador já está em execução; feche-o e tente novamente"; falha de hash → "O download foi corrompido; tente novamente"; fonte indisponível → "O catálogo não respondeu; verifique a conexão".
7. **Progresso:** porcentagem apenas quando o winget emitir um número; caso contrário, indicador indeterminado.

---

## 9. Telas sem mockup

### 9.1 Histórico
- Cabeçalho: "Histórico" (`AsaHeadlineLgTextStyle`) + descrição "Lotes executados neste dispositivo" + botão ghost "Exportar log".
- Corpo: lista de cards (raio 12, `AsaSurfaceCard`), um por lote: data/hora, `N itens`, resultado (chips verde/âmbar/vermelho), duração, origem dos pacotes. Clique expande o log em `AsaLabelCodeTextStyle` (`AsaSurfaceSunken`, raio 8, altura máx 320, com rolagem) e botão "Copiar".
- Persistência: `%LOCALAPPDATA%\Asa\history.json`, últimos 50 lotes.
- Vazio: ícone de histórico, "Nenhuma operação registrada ainda." + botão "Ir para Atualizações".
- Aviso de MSIX que não atualizou aparece como `InfoBar` dispensável no topo, persistindo além do auto-hide da barra.

### 9.2 Configurações
Cards (raio 12) com título `AsaHeadlineMdTextStyle` e descrição `AsaBodySmTextStyle`:
1. **Instalação** — `ToggleSwitch` "Instalação silenciosa" + texto do estado real; aviso de que o Asa roda elevado.
2. **Winget** — versão detectada (badge `AsaLabelCodeTextStyle`), caminho do executável (selecionável), botão ghost "Verificar novamente" (desabilitado com `ProgressRing` de 16 px durante a verificação).
3. **Catálogo** — origem ativa (chips `winget`/`msstore`), "atualizado há {X} min", botão ghost "Atualizar catálogo".
4. **Dados** — pasta de preferências e histórico, botões "Abrir pasta" e "Limpar cache de metadados" (confirmação via `ContentDialog`).
5. **Sobre** — versão do Asa, créditos de fontes (OFL), link do projeto.

**Não existe seletor de tema** (D1). Não existe seção de tema claro/escuro.

---

## 10. Acessibilidade

- **Contraste:** texto ≥ 4,5:1 (tabelas da seção 2); ícones e indicadores ≥ 3:1; anel de foco `AsaAction` de 2 px / 13:1 em **todo** elemento focável.
- **Alvos:** mínimo 40 × 40 px; linhas de 56 px mantêm clique confiável.
- **Alto Contraste:** `ThemeDictionaries` com `HighContrast` mapeando todas as cores para `SystemColor*` (`SystemColorWindowColor`, `SystemColorWindowTextColor`, `SystemColorHighlightColor`); glow/pulse desativados; nenhuma informação dependente só de cor.
- **Teclado:** `Ctrl+1..5` (páginas), `F5` (atualizar), `Ctrl+F` (focar filtro), `Esc` (limpar filtro/busca), setas e `Tab` entre linhas e controles, `Shift+F10` para menu de contexto.
- **Leitor de tela:** `AutomationProperties.Name` na linha inteira (nome · versão · estado), na barra de atividade (`LiveSetting="Polite"`) e em todo ícone-botão; `ProgressRing` nomeado.
- **Movimento:** respeitar `UISettings.AnimationsEnabled`; nenhuma animação essencial para entender o estado.

---

## 11. Checklist de conformidade visual

- [ ] Nenhum valor de cor/tipografia/raio literal fora deste documento (buscar por `#` e por valores numéricos de `CornerRadius` no XAML).
- [ ] Todas as cores expostas em `Styles/Tokens.xaml` com `ThemeDictionaries` (Dark + HighContrast).
- [ ] Versões, IDs e atalhos sempre em `AsaLabelCodeTextStyle`/`AsaLabelBadgeTextStyle`.
- [ ] Nenhum texto informativo com `AsaTextTertiary` sobre `AsaSurfaceActive`.
- [ ] Glow apenas nos elementos permitidos; nenhum glow em linha de lista.
- [ ] Densidade: linha 56 px, sidebar 240 px, title bar 48 px, barra de status 32 px.
- [ ] Janela em 900×600 sem clipping e com a coluna de ação visível.
- [ ] Todos os alvos ≥ 40 × 40 px.
- [ ] Anel de foco visível em 100% dos controles focáveis.
- [ ] Toda tela com estados vazio, carregando, erro e sucesso implementados.
- [ ] Nenhum dado exibido que viole a seção 8.
