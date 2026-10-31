# Bakes the monitoring/journey-store SQL scripts INTO the image (instead of a bind mount) so a
# content change under Sql/**/*.sql or init-sql.sh changes this image's layers, and `docker
# compose up --build` (the documented top-level command, see docker-compose.yml's header comment)
# recreates and reruns this container automatically - a bind mount's content is invisible to
# Compose's "did this service's config change" check, so new/changed scripts were previously only
# applied by manually passing --force-recreate.
FROM mcr.microsoft.com/mssql/server:2022-latest
USER root
COPY src/JourneyMonitor.Infrastructure/Sql /scripts
COPY docker/init-sql.sh /init-sql.sh
# Strip CR so this still runs correctly when checked out with CRLF line endings on Windows.
RUN sed -i 's/\r$//' /init-sql.sh && chmod +x /init-sql.sh
ENTRYPOINT ["/bin/bash", "/init-sql.sh"]
