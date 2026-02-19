GiroServerOps is a Windows Presentation Foundation (WPF) desktop application built with .NET 10.0. It serves as a real-time monitoring dashboard designed to track system hardware resources and SQL Server performance metrics. The application features a modern UI with an animated side menu and visually indicates the health of the server through dynamic Key Performance Indicator (KPI) cards.

Key Features
System Resource Monitoring: Continuously tracks the host machine's total CPU usage and available/used Physical RAM utilizing Windows Management Instrumentation (WMI) and Performance Counters.

SQL Server Process Tracking: Automatically detects the active SQL Server instance process (sqlservr) and isolates its specific CPU consumption.

Database Storage Analysis: Connects directly to the SQL Server to execute queries that calculate overall used/free disk space (via xp_fixeddrives) and monitors the size ratio between Transaction Logs and Database files (LOG vs. DB).

Dynamic KPI Alerts: Evaluates metrics in real-time (refreshing every 2 seconds) and assigns health statuses (Ok, Warning, Critical) based on predefined thresholds (e.g., RAM usage over 90% or a Log size exceeding 80% of the DB triggers a Critical alert).

Modern Interface: Implements a custom, borderless window design with rounded corners and a collapsible hamburger menu driven by fluid storyboard animations.

Requirements and Technologies
Framework: .NET 10.0 (Windows-only support).

User Interface: WPF (Windows Presentation Foundation).

Database Access: Microsoft.Data.SqlClient for executing administrative metric queries against the database.

System Interactions: System.Management for WMI queries and System.Diagnostics.PerformanceCounter for hardware telemetry.

Configuration and Permissions [Inferencia]
The dashboard requires a valid SQL Server connection string upon initialization to gather database-specific metrics. Because the application executes low-level system queries (such as EXEC master..xp_fixeddrives and querying sys.master_files), it is an expected behavior that the SQL credentials provided must have elevated administrative privileges (such as the sysadmin role) to function correctly. Furthermore, running Windows Performance Counters and WMI queries may require the application itself to be executed with Windows Administrator privileges.
