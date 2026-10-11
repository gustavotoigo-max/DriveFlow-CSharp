namespace DriveFlow.Core;

/// <summary>Falha passageira (rede, 429, 5xx): o envio tenta de novo.</summary>
public class TemporaryException(string message) : Exception(message);

/// <summary>Erro definitivo do Google Drive, mostrado ao usuário.</summary>
public class DriveException(string message) : Exception(message);

/// <summary>A sessão retomável não existe mais (HTTP 404/410).</summary>
public class ExpiredSessionException() : Exception("Sessão de upload expirada.");

public class DuplicateNameException(string message) : Exception(message);

public class LocalFileChangedException(string message) : Exception(message);

/// <summary>Só mensagens sem credenciais podem ir para a tela ou o log.</summary>
public class AuthException(string message) : Exception(message);
