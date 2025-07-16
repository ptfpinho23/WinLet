const express = require('express');
const fs = require('fs');
const path = require('path');

const app = express();
const port = process.env.PORT || 3000;
const logPath = process.env.LOG_PATH || 'C:\\Logs\\MyWebApp';

// Ensure log directory exists
if (!fs.existsSync(logPath)) {
    fs.mkdirSync(logPath, { recursive: true });
}

// Simple logging function
function log(message) {
    const timestamp = new Date().toISOString();
    const logMessage = `[${timestamp}] ${message}\n`;
    
    // Log to console
    console.log(logMessage.trim());
    
    // Log to file
    try {
        const logFile = path.join(logPath, 'app.log');
        fs.appendFileSync(logFile, logMessage);
    } catch (err) {
        console.error('Failed to write to log file:', err.message);
    }
}

// Middleware for logging requests
app.use((req, res, next) => {
    log(`${req.method} ${req.url} - ${req.ip}`);
    next();
});

// Routes
app.get('/', (req, res) => {
    const response = {
        message: 'Hello from My Web App!',
        timestamp: new Date().toISOString(),
        environment: process.env.NODE_ENV || 'development',
        port: port,
        uptime: process.uptime()
    };
    
    log('Served homepage request');
    res.json(response);
});

app.get('/health', (req, res) => {
    const health = {
        status: 'healthy',
        timestamp: new Date().toISOString(),
        uptime: process.uptime(),
        memory: process.memoryUsage(),
        version: process.version
    };
    
    log('Health check requested');
    res.json(health);
});

app.get('/api/status', (req, res) => {
    res.json({
        service: 'my-web-app',
        status: 'running',
        timestamp: new Date().toISOString(),
        environment: process.env.NODE_ENV || 'development'
    });
});

// Error handling
app.use((err, req, res, next) => {
    log(`Error: ${err.message}`);
    res.status(500).json({ error: 'Internal Server Error' });
});

// 404 handler
app.use((req, res) => {
    log(`404 - Not Found: ${req.url}`);
    res.status(404).json({ error: 'Not Found' });
});

// Start server
app.listen(port, () => {
    log(`🚀 My Web App is running on port ${port}`);
    log(`📝 Logs are being written to: ${logPath}`);
    log(`🌍 Environment: ${process.env.NODE_ENV || 'development'}`);
    
    // Handle graceful shutdown
    process.on('SIGTERM', () => {
        log('SIGTERM received - shutting down gracefully');
        process.exit(0);
    });
    
    process.on('SIGINT', () => {
        log('SIGINT received - shutting down gracefully');
        process.exit(0);
    });
}); 