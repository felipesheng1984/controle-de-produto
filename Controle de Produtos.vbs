' ====================================================================
'  Controle de Produtos - Re-Teck Brasil
'  Abre o programa SEM janela preta (console).
'  Duplo-clique NESTE arquivo.
'  - Roda o servidor (Produtos.exe = tipo "janela", nao abre console)
'  - Espera ficar pronto e abre o navegador
'  - Se ja estiver rodando, so abre o navegador (nao abre outro)
' ====================================================================
Option Explicit
Dim sh, fso, baseDir, srvDir, exePath, url, i
Set sh  = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
baseDir = fso.GetParentFolderName(WScript.ScriptFullName)
srvDir  = baseDir & "\server-csharp"
exePath = srvDir & "\bin\Release\net8.0\Produtos.exe"
url = "http://localhost:5011"

If Not ServerUp(url) Then
    ' o diretorio de trabalho TEM de ser server-csharp (e onde esta o wwwroot)
    sh.CurrentDirectory = srvDir
    ' 1a vez / apos limpar bin: compila (escondido, espera terminar)
    If Not fso.FileExists(exePath) Then
        sh.Run "dotnet build -c Release", 0, True
    End If
    If fso.FileExists(exePath) Then
        ' exe WinExe (sem console) + janela 0 = totalmente invisivel
        sh.Run """" & exePath & """", 0, False
        ' espera subir (ate ~40s na 1a vez)
        For i = 1 To 80
            WScript.Sleep 500
            If ServerUp(url) Then Exit For
        Next
    End If
End If

' abre no navegador padrao
sh.Run url, 1, False

Function ServerUp(u)
    On Error Resume Next
    Dim h
    Set h = CreateObject("MSXML2.XMLHTTP")
    h.Open "GET", u & "/api/state", False
    h.Send
    ServerUp = (Err.Number = 0) And (h.Status = 200)
    On Error GoTo 0
End Function
