@echo off
cd /d C:\BackupUtility
ADShield.exe --backup --computer LOCALVM --type Full --password un4GET@ble > C:\BackupUtility\backup_test.log 2>&1
