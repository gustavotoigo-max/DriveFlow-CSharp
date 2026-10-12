# DriveFlow (C#)

Nova versão do DriveFlow em C# (.NET 8), para abrir rápido e não depender do pacote Python.
A versão atual, em Python, continua em [DriveFlow](https://github.com/gustavotoigo-max/DriveFlow) até esta substituí-la.

## O que já existe

`src/DriveFlow.Core`, a parte sem tela, portada da versão Python com o mesmo comportamento:

- **Fila** (`Storage/Database.cs`): SQLite com o mesmo esquema da versão Python, para reaproveitar a fila e as configurações.
- **Cofre** (`Storage/Vault.cs`): DPAPI do usuário do Windows, compatível com o que a versão Python gravou.
- **Google Drive** (`Drive/DriveClient.cs`): listagem de pastas, criação de pasta e upload retomável.
- **Envio** (`Upload/UploadEngine.cs`, `Upload/UploadManager.cs`): blocos de 8 MiB, retomada pelo que o Google confirmou, novas tentativas, limite de velocidade, pausa e cancelamento.
- **Login Google** (`Auth/GoogleAuth.cs`): navegador + retorno em 127.0.0.1 com PKCE, renovação do token e leitura do `account.bin` da versão Python.
- **WhatsApp** (`Notify/WhatsApp.cs`): aviso "PASTA UPLOAD FINALIZADO" pelo CallMeBot, com a mesma configuração.

`src/DriveFlow.App`, a janela em WPF no visual Nexotool (cabeçalho azul-marinho como barra de título, tema claro/escuro):

- **Transferências**: arquivos e pastas para enviar, árvore de pastas do Drive (Voltar, Atualizar, Nova pasta, Usar esta pasta) e a fila com progresso, velocidade e controles.
- **Histórico**: envios concluídos, com copiar link, abrir no Drive e copiar ID.
- **Configurações**: tema, uploads simultâneos, bloco, tentativas, permissão, WhatsApp e conta.
- **Atividade**: os eventos registrados, com exportação.

Ainda faltam o Compactar com WinRAR, o monitor Firebase e o pareamento do celular, o atualizador e a migração dos dados da versão Python.

## Compilar e testar

Precisa do [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet test
Publicar.bat
```

`Publicar.bat` gera `publish\DriveFlow\DriveFlow.exe` em pasta (já com o .NET dentro, sem instalar nada) e abre o pacote no modo de teste rápido (`--smoke-test`).

## Instalador privado

`Instalador.bat` roda o `Publicar.bat` e gera `publish\DriveFlow-Setup-PRIVADO-vX.exe` com o [Inno Setup 6](https://jrsoftware.org/isinfo.php). Ele só gera se o `oauth_client.json` estiver na raiz, para o instalado já entrar com o Google. Esse instalador leva a credencial: não vai para o GitHub nem para releases.

O login embutido usa o arquivo `oauth_client.json` (cliente OAuth de "Aplicativo para computador") na raiz do repositório. Ele fica fora do Git; sem ele, o app pede o JSON ao conectar.

Enquanto as duas versões convivem, os dados ficam em `%LOCALAPPDATA%\DriveFlowNet`, separados da versão Python. A variável `DRIVEFLOW_DATA_DIR` troca essa pasta.
