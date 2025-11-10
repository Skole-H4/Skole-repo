# syntax=docker/dockerfile:1

FROM confluentinc/cp-kafka-connect:7.7.1

# Install the Confluent JDBC sink connector
RUN confluent-hub install --no-prompt confluentinc/kafka-connect-jdbc:10.7.4

# Add Microsoft SQL Server JDBC driver (MIT License)
RUN curl -L -o /usr/share/java/mssql-jdbc.jar \
    https://repo1.maven.org/maven2/com/microsoft/sqlserver/mssql-jdbc/12.6.1.jre11/mssql-jdbc-12.6.1.jre11.jar

# Ensure plugin directory exists for locally mounted configs
RUN mkdir -p /etc/kafka-connect/jars
