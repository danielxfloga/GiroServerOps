QuickSave is a Windows Presentation Foundation (WPF) desktop application built with .NET Framework 4.8. It is designed for personal finance management, allowing users to track their daily income and expenses, organize transactions into custom categories, and visualize their financial health through interactive charts.

Key Features
Transaction Tracking: Allows users to record, view, and delete financial transactions, seamlessly classifying them as either income or expenses.

Category Management: Users can create new transaction categories, edit existing ones, and assign specific color codes to them for better visual organization.

Interactive Dashboards: Generates detailed visual reports using pie charts (powered by the LiveCharts library) to display the distribution of income and expenses.

Date Filtering: Includes built-in date range filters to analyze financial movements and overall balances over specific time periods.

PDF Export: Enables the exportation of graphical reports, filtered date ranges, and balance summaries directly into PDF documents using the PDFsharp library.

Requirements and Technologies
Framework: .NET Framework 4.8 (WPF)

Database: Microsoft SQL Server (utilizes System.Data.SqlClient for direct data access).

Key Libraries: LiveCharts.Wpf (for data visualization), PDFsharp-WPF (for document generation), and SkiaSharp.

Configuration and Environment Variables 
The database connection relies on a connection string specifically named QuickSaveDb, which must be defined within the application's App.config file to establish communication with the SQL Server.
