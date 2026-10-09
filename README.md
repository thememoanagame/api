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

## ❓ What is My Project?

## ⚡ Getting Started

## 🔧 Building and Running

### 🔨 Build the Project

### ▶ Running and Settings

## 🤝 Collaborate with My Project
