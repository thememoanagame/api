# Memoana API

## Testes e validação

O submódulo `modules/themes` deve estar inicializado para executar a aplicação real e o teste `StaticThemeProviderTests`.

```text
git submodule update --init --recursive
dotnet build memoana.slnx --no-restore -m:1
dotnet test tests/memoana.tests/memoana.tests.csproj --no-restore -v:minimal
```

O `JoinRoom` retorna um token opaco de participação. Os endpoints de assets aceitam esse valor em `X-Player-Token`; `X-Player-Id` não é uma credencial válida. O token vincula o acesso ao estado em memória da room e é invalidado quando o participante sai.

Essa é a garantia compatível com o MVP sem contas ou autenticação tradicional: quem obtiver o token durante a participação pode usá-lo enquanto a room existir. Não há persistência, revogação distribuída ou proteção de identidade entre processos.

## Contrato HTTP

`GET /api/game/themes` lista o catálogo gerado pelo submódulo `modules/themes`. Cada item contém o GUID estável, nome, disponibilidade, quantidade de cartas jogáveis, dificuldades compatíveis e a referência da capa. `GET /api/game/difficulties` retorna as regras atuais: Easy = 6 pares/12 cartas, Medium = 10/20 e Hard = 15/30.

`POST /api/game/rooms` aceita `{ "mode": "Time|PVP|AI", "difficulty": "Easy|Medium|Hard", "themeId": "guid" }`. `themeId` é opcional por compatibilidade; quando omitido, é usado `Themes:DefaultTheme`. GUID inexistente, indisponível ou sem conteúdo suficiente é rejeitado com erro estruturado. A resposta devolve a sala, o tema efetivo, a quantidade de pares/cartas e o estado `Waiting`.

O ingresso continua sendo feito somente pelo SignalR em `/gameHub`, pelo método `JoinRoom(roomId)`. A resposta contém `AccessToken`, que deve ser enviado no cabeçalho `X-Player-Token` para `GET /api/game/rooms/{roomId}` e para o manifesto/assets. O token não deve ser colocado em URL, logs ou mensagens para outros participantes.

O manifesto de assets contém referências relativas estáveis para `GET /api/game/rooms/{roomId}/assets/{assetToken}`. Os pares não são expostos no manifesto nem no estado enquanto as cartas não forem reveladas.

## Protocolo SignalR

O cliente invoca `JoinRoom`, `GetState`, `AssetsReady`, `FlipCard(roomId, position)` e `LeaveRoom`. O servidor publica `PlayerJoined`, `PlayerLeft`, `GamePreparing`, `AssetsAvailable`, `AssetsReady`, `GameStarted`, `CardRevealed`, `PairMatched`, `PairMissed`, `TurnChanged`, `ScoreUpdated` e `GameFinished`. Falhas retornam `GameOperationResult` com `ErrorCode`/`ErrorMessage` e também o evento `Error` ao chamador.

Time inicia quando o único participante confirma os assets e termina por todos os pares ou pelo temporizador. PVP prepara após dois participantes ingressarem e só inicia quando ambos confirmam os assets. AI prepara com um humano; os movimentos da IA são processados pelo mesmo `FlipCard` autoritativo após `GameStarted`.

O estado ativo é mantido em memória e persistido em SQLite. Desconectar remove apenas a associação temporária do transporte; sair voluntariamente remove o participante. Reiniciar recupera salas persistidas, mas escalar a API ainda exige coordenação externa.

## Persistência e recuperação

O estado das salas é persistido em SQLite por `MemoAnaDbContext` e `SqliteGameStateStore`. O caminho padrão é `data/memoana.db` e pode ser alterado por `Persistence:DatabasePath` ou `Persistence__DatabasePath`. A aplicação cria o diretório e aplica as migrações pendentes durante a inicialização. A migração inicial está em `src/memoana/Persistence/Migrations`.

O snapshot persistido preserva a ordem do tabuleiro, correspondências internas, cartas reveladas/encontradas, assets selecionados, participantes, hashes dos tokens, prontidão, pontuação, turno, início e prazo. Os bytes dos assets selecionados são mantidos no snapshot para garantir que uma restauração não escolha outro conjunto quando o catálogo mudar.

`JoinRoom(roomId)` mantém o fluxo inicial. Para reconectar uma participação existente, o cliente invoca `ReconnectRoom(roomId, playerId, accessToken)`. O `PlayerId` é estável na sala; o `ConnectionId` só representa o transporte atual. O servidor rejeita uma segunda conexão simultânea para o mesmo participante. Desconexão de transporte remove apenas a associação temporária; `LeaveRoom` remove a participação e revoga o token.

A API continua sendo uma instância única para coordenação das alterações em memória e SignalR. SQLite permite recuperar o estado após reinício, mas não substitui um armazenamento compartilhado, backplane SignalR ou lock distribuído para múltiplas instâncias. Em Docker, monte um volume no diretório configurado para `Persistence__DatabasePath`.

Para aplicar migrações manualmente, use `dotnet ef database update --project src/memoana/memoana.csproj --startup-project src/memoana/memoana.csproj`. Não use `EnsureDeleted` em ambientes persistentes.

## ❓ What is My Project?

## ⚡ Getting Started

## 🔧 Building and Running

### 🔨 Build the Project

### ▶ Running and Settings

## 🤝 Collaborate with My Project
