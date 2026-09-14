#!/usr/bin/env bash
# Mostra o status do servico de sincronizacao e os logs em tempo real.
echo "===================================================="
echo " MonitorFix - Status da sincronizacao com GitHub"
echo "===================================================="
echo
systemctl --user status monitorfix-sync.service --no-pager
echo
echo "----------------------------------------------------"
echo " Logs em tempo real (Ctrl+C para sair)"
echo "----------------------------------------------------"
journalctl --user -u monitorfix-sync.service -f -n 30
