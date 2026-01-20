import { useEffect, useState, useCallback, useRef } from 'react';
import * as signalR from '@microsoft/signalr';
import type { MetricsData, ProcessMetaData, ProcessInfo } from '../types';

const HUB_URL = 'http://localhost:35179/hubs/metrics';

export const useMetricsHub = () => {
  const [connection, setConnection] = useState<signalR.HubConnection | null>(null);
  const [metricsData, setMetricsData] = useState<MetricsData | null>(null);
  const [isConnected, setIsConnected] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const metaMapRef = useRef<Map<number, ProcessMetaData['processes'][number]>>(new Map());

  const mergeMeta = useCallback((processes: ProcessInfo[]): ProcessInfo[] => {
    if (metaMapRef.current.size === 0) return processes;
    return processes.map((p) => {
      const meta = metaMapRef.current.get(p.processId);
      if (!meta) return p;
      return {
        ...p,
        commandLine: meta.commandLine,
        displayName: meta.displayName
      };
    });
  }, []);

  useEffect(() => {
    const newConnection = new signalR.HubConnectionBuilder()
      .withUrl(HUB_URL)
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Information)
      .build();

    setConnection(newConnection);

    return () => {
      if (newConnection.state === signalR.HubConnectionState.Connected) {
        newConnection.stop();
      }
    };
  }, []);

  useEffect(() => {
    if (!connection) return;

    const startConnection = async () => {
      try {
        await connection.start();
        setIsConnected(true);
        setError(null);
        console.log('SignalR Connected');
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Connection failed');
        console.error('SignalR Connection Error:', err);
        setTimeout(startConnection, 5000);
      }
    };

    connection.on('ReceiveProcessMetrics', (data: MetricsData) => {
      setMetricsData({
        ...data,
        processes: mergeMeta(data.processes)
      });
    });

    connection.on('ReceiveProcessMetadata', (data: ProcessMetaData) => {
      data.processes.forEach((p) => {
        metaMapRef.current.set(p.processId, p);
      });

      setMetricsData((prev) => {
        if (!prev) return prev;
        return {
          ...prev,
          processes: mergeMeta(prev.processes)
        };
      });
    });

    connection.onreconnecting(() => {
      setIsConnected(false);
      setError('Reconnecting...');
    });

    connection.onreconnected(() => {
      setIsConnected(true);
      setError(null);
    });

    connection.onclose(() => {
      setIsConnected(false);
      setError('Connection closed');
    });

    startConnection();
  }, [connection]);

  const disconnect = useCallback(async () => {
    if (connection && connection.state === signalR.HubConnectionState.Connected) {
      await connection.stop();
      setIsConnected(false);
    }
  }, [connection]);

  return {
    metricsData,
    isConnected,
    error,
    disconnect,
  };
};
