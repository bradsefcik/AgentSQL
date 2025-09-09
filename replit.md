# AgentSQL - SQL Generator

## Overview
AgentSQL is an ASP.NET Core 8.0 web application that generates parameterized SQL DML statements and stored procedures from CREATE TABLE scripts. The application supports multiple SQL dialects including SQL Server, PostgreSQL, MySQL, MariaDB, SQLite, Oracle, Snowflake, and Spark SQL.

## Project Architecture
- **Frontend**: ASP.NET Core Razor Pages with static files (CSS, service worker for PWA)
- **Backend**: Minimal API with endpoints for analytics, admin, licensing, and Stripe integration
- **Dependencies**: 
  - SendGrid (v9.29.3) for email notifications
  - Stripe.net (v42.8.0) for payment processing
- **Configuration**: Uses environment variables and appsettings.json for API keys and settings

## Current State
✅ **Fully configured and running**
- .NET 8.0 SDK installed and configured
- NuGet packages restored (SendGrid, Stripe.net) 
- Application configured for Replit environment (port 5000, all interfaces)
- Workflow configured and running successfully
- Deployment settings configured for production

## Development Setup
- **Port**: 5000 (configured for Replit proxy)
- **Host**: 0.0.0.0 (allows Replit iframe access)
- **Environment**: Development mode enabled
- **Build**: Uses dotnet build/run commands

## Key Features
- Multi-dialect SQL generation (8 supported databases)
- Pro/Basic tier licensing system with Stripe integration
- Analytics tracking (JSON-based)
- Admin panel for analytics and management
- Progressive Web App (PWA) capabilities
- Email notifications via SendGrid

## API Integration Requirements
- **Stripe**: Requires STRIPE_SECRET_KEY, STRIPE_PRICE_ID, STRIPE_WEBHOOK_SECRET
- **SendGrid**: Requires SENDGRID_API_KEY, SendGrid:FromEmail configuration
- **Licensing**: Requires License__Secret for token validation
- **Admin**: Requires ADMIN_KEY for admin panel access

## Recent Changes (Sep 9, 2025)
- Imported from GitHub and configured for Replit environment
- Modified launchSettings.json to use port 5000 with 0.0.0.0 binding
- Added Kestrel configuration for proper host binding in Program.cs
- Set up workflow for continuous development server
- Configured autoscale deployment for production

## File Structure
```
AgentSQL/
├── Pages/          # Razor pages and views
├── Properties/     # Launch settings
├── wwwroot/        # Static files (CSS, images, service worker)
├── AgentSQL.csproj # Project file with dependencies
├── Program.cs      # Application entry point with API endpoints
└── appsettings.json # Configuration settings
```