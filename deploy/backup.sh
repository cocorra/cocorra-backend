#!/bin/sh
# Reference copy of the server's /opt/cocorra/infra/backup.sh. The server runs ITS copy:
# after changing this file, copy it to /opt/cocorra/infra/backup.sh (mode 755).
#
# deploy/deploy.sh runs it before every deploy and reads the last line,
#   BACKUP_DB=<full path of the .bak.gz>
# to print exact restore steps if the deploy rolls back. Keep that line last.
set -eu

DB_NAME="CocorraDb"
DIR="/opt/cocorra/backups"
KEEP_DAYS=7
TS=$(date +%Y%m%d-%H%M)

echo "[$(date)] Starting backup"

docker exec cocorra_db mkdir -p /var/opt/mssql/backup
docker exec cocorra_db sh -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -Q "BACKUP DATABASE ['"$DB_NAME"'] TO DISK = N'\''/var/opt/mssql/backup/latest.bak'\'' WITH CHECKSUM, INIT"'
docker cp cocorra_db:/var/opt/mssql/backup/latest.bak "$DIR/db-$TS.bak"
gzip "$DIR/db-$TS.bak"

docker run --rm -v infra_minio_data:/data:ro alpine:3.22 tar czf - -C /data . > "$DIR/minio-$TS.tar.gz"

find "$DIR" -name 'db-*.bak.gz' -mtime +$KEEP_DAYS -delete
find "$DIR" -name 'minio-*.tar.gz' -mtime +$KEEP_DAYS -delete

echo "[$(date)] Done: db-$TS.bak.gz, minio-$TS.tar.gz"

# Machine-readable, for deploy.sh. Must stay the last line of output.
echo "BACKUP_DB=$DIR/db-$TS.bak.gz"
