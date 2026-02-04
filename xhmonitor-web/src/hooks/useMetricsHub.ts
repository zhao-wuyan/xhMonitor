import { useEffect, useState, useCallback, useRef } from 'react';
import * as signalR from '@microsoft/signalr';
import type { DiskUsage, MetricsData, ProcessMetaData, ProcessInfo, SystemUsage } from '../types';
import { METRICS_HUB_URL } from '../config/endpoints';

const HUB_URL = METRICS_HUB_URL;

export const useMetricsHub = () => {
  const [connection, setConnection] = useState<signalR.HubConnection | null>(null);
  const [metricsData, setMetricsData] = useState<MetricsData | null>(null);
  const [systemUsage, setSystemUsage] = useState<SystemUsage | null>(null);
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

    connection.on('ReceiveSystemUsage', (data: Record<string, unknown>) => {
      const toNullableNumber = (value: unknown): number | null => {
        if (value === null || value === undefined) return null;
        const n = Number(value);
        return Number.isFinite(n) ? n : null;
      };

      const rawDisks = (data.disks ?? data.Disks) as unknown;
      const disks: DiskUsage[] | undefined = Array.isArray(rawDisks)
        ? rawDisks
            .map((item) => {
              const d = item as Record<string, unknown>;
              const name = String(d.name ?? d.Name ?? '').trim();
              if (!name) return null;
              return {
                name,
                totalBytes: toNullableNumber(d.totalBytes ?? d.TotalBytes),
                usedBytes: toNullableNumber(d.usedBytes ?? d.UsedBytes),
                readSpeed: toNullableNumber(d.readSpeed ?? d.ReadSpeed),
                writeSpeed: toNullableNumber(d.writeSpeed ?? d.WriteSpeed),
              } satisfies DiskUsage;
            })
            .filter((d): d is DiskUsage => Boolean(d))
        : undefined;

      const powerSchemeRaw = data.powerSchemeIndex ?? data.PowerSchemeIndex;
      const usage: SystemUsage = {
        timestamp: (data.timestamp ?? data.Timestamp ?? new Date().toISOString()) as string,
        totalCpu: Number(data.totalCpu ?? data.TotalCpu ?? 0),
        totalGpu: Number(data.totalGpu ?? data.TotalGpu ?? 0),
        totalMemory: Number(data.totalMemory ?? data.TotalMemory ?? 0),
        totalVram: Number(data.totalVram ?? data.TotalVram ?? 0),
        disks,
        maxMemory: Number(data.maxMemory ?? data.MaxMemory ?? 0),
        maxVram: Number(data.maxVram ?? data.MaxVram ?? 0),
        uploadSpeed: Number(data.uploadSpeed ?? data.UploadSpeed ?? 0),
        downloadSpeed: Number(data.downloadSpeed ?? data.DownloadSpeed ?? 0),
        powerAvailable: Boolean(data.powerAvailable ?? data.PowerAvailable ?? false),
        totalPower: Number(data.totalPower ?? data.TotalPower ?? 0),
        maxPower: Number(data.maxPower ?? data.MaxPower ?? 0),
        powerSchemeIndex: powerSchemeRaw == null ? null : Number(powerSchemeRaw),
      };
      setSystemUsage(usage);
    });

    connection.onreconnecting(() => {
      setIsConnected(false);
      setError('reconnecting');
    });

    connection.onreconnected(() => {
      setIsConnected(true);
      setError(null);
    });

    connection.onclose(() => {
      setIsConnected(false);
      setError('connectionClosed');
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
    systemUsage,
    isConnected,
    error,
    disconnect,
  };
};
