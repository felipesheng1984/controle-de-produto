' ====================================================================
'  Parar o Controle de Produtos (encerra o servidor em segundo plano).
'  So precisa disto se quiser fechar de verdade ou atualizar o programa.
'  Duplo-clique NESTE arquivo (nao mostra janela).
' ====================================================================
Dim sh
Set sh = CreateObject("WScript.Shell")
sh.Run "powershell -NoProfile -WindowStyle Hidden -Command ""Get-NetTCPConnection -LocalPort 5007 -State Listen -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force }""", 0, True
