@echo off
REM ====================================================================
REM  Controle de Produtos - Re-Teck Brasil
REM  Duplo-clique para abrir: sobe o servidor e abre o navegador.
REM  (precisa do .NET 8 SDK instalado)
REM ====================================================================
setlocal
cd /d "%~dp0server-csharp"

REM Abre o navegador na pagina depois de alguns segundos.
start "" /b cmd /c "timeout /t 3 >nul & start http://localhost:5011"

echo Iniciando Controle de Produtos... (feche esta janela para encerrar)
dotnet run -c Release
