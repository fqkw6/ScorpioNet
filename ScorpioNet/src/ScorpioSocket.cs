﻿using System;
using System.Collections.Generic;
using System.Net.Sockets;
namespace Scorpio.Net {
    public class ScorpioSocket {
        private const int MAX_BUFFER_LENGTH = 8192;                             // 缓冲区大小
        private byte[] m_RecvTokenBuffer = new byte[MAX_BUFFER_LENGTH];         // 已经接收的数据总缓冲
        private int m_RecvTokenSize = 0;                                        // 接收总数据的长度
        private bool m_Sending;                                                 // 是否正在发送消息
        private Socket m_Socket = null;                                         // Socket句柄
        private Queue<byte[]> m_SendQueue = new Queue<byte[]>();                // 发送消息队列
        private object m_SendSync = new object();                               // 发送消息线程锁
        private SocketAsyncEventArgs m_RecvEvent = null;                        // 异步接收消息
        private SocketAsyncEventArgs m_SendEvent = null;                        // 异步发送消息
        private ScorpioConnection m_Connection = null;                          // 连接
        private bool m_LengthIncludesLengthFieldLength;                         // 数据总长度是否包含
        public ScorpioSocket(Socket socket, bool lengthIncludesLengthFieldLength) {
            m_Socket = socket;
            m_LengthIncludesLengthFieldLength = lengthIncludesLengthFieldLength;
            m_SendEvent = new SocketAsyncEventArgs();
            m_SendEvent.Completed += SendAsyncCompleted;
            m_RecvEvent = new SocketAsyncEventArgs();
            m_RecvEvent.SetBuffer(new byte[MAX_BUFFER_LENGTH], 0, MAX_BUFFER_LENGTH);
            m_RecvEvent.Completed += RecvAsyncCompleted;
        }
        //设置socket句柄
        public void SetConnection(ScorpioConnection connection) {
            m_Connection = connection;
            m_Sending = false;
            m_RecvTokenSize = 0;
            Array.Clear(m_RecvTokenBuffer, 0, m_RecvTokenBuffer.Length);
            Array.Clear(m_RecvEvent.Buffer, 0, m_RecvEvent.Buffer.Length);
            m_SendQueue.Clear();
            BeginReceive();
        }
        public Socket GetSocket() {
            return m_Socket;
        }
        public void Send(byte type, short msgId, byte[] data) {
            Send(type, msgId, data, 0, data != null ? data.Length : 0);
        }
        //发送协议
        public void Send(byte type, short msgId, byte[] data, int offset, int length) {
            if (!m_Socket.Connected) { return; }
            int count = length + 7;                                             //协议头长度  数据长度int(4个字节) + 数据类型byte(1个字节) + 协议IDshort(2个字节)
            byte[] buffer = new byte[count];
            Array.Copy(BitConverter.GetBytes(m_LengthIncludesLengthFieldLength ? count : count - 4), buffer, 4);            //写入数据长度
            buffer[4] = type;                                                   //写入数据类型
            Array.Copy(BitConverter.GetBytes(msgId), 0, buffer, 5, 2);          //写入数据ID
            if (data != null) Array.Copy(data, offset, buffer, 7, length);      //写入数据内容
            lock (m_SendQueue) { m_SendQueue.Enqueue(buffer); }
            ScorpioThreadPool.CreateThread(() => { BeginSend(); });
        }
        void BeginSend() {
            lock (m_SendSync) {
                if (m_Sending || m_SendQueue.Count <= 0) return;
                m_Sending = true;
                byte[] data = null;
                lock (m_SendQueue) { data = m_SendQueue.Dequeue(); }
                SendInternal(data, 0, data.Length);
            }
        }
        void SendInternal(byte[] data, int offset, int length) {
            var completedAsync = false;
            try {
                if (data == null)
                    m_SendEvent.SetBuffer(offset, length);
                else
                    m_SendEvent.SetBuffer(data, offset, length);
                completedAsync = m_Socket.SendAsync(m_SendEvent);
            } catch (System.Exception ex) {
                LogError("发送数据出错 : " + ex.ToString());
                Disconnect(SocketError.SocketError);
            }
            if (!completedAsync) {
                m_SendEvent.SocketError = SocketError.Fault;
                SendAsyncCompleted(this, m_SendEvent);
            }
        }
        void SendAsyncCompleted(object sender, SocketAsyncEventArgs e) {
            if (e.SocketError != SocketError.Success) {
                LogError("发送数据出错 : " + e.SocketError);
                Disconnect(e.SocketError);
                return;
            }
            lock (m_SendSync) {
                if (e.Offset + e.BytesTransferred < e.Count) {
                    SendInternal(null, e.Offset + e.BytesTransferred, e.Count - e.BytesTransferred - e.Offset);
                } else {
                    m_Sending = false;
                    BeginSend();
                }
            }
        }
        //开始接收消息
        void BeginReceive() {
            try {
                if (!m_Socket.ReceiveAsync(m_RecvEvent)) {
                    LogError("接收数据失败");
                    Disconnect(SocketError.SocketError);
                }
            } catch (System.Exception e) {
                LogError("接收数据出错 : " + e.ToString());
                Disconnect(SocketError.SocketError);
            }
        }
        void RecvAsyncCompleted(object sender, SocketAsyncEventArgs e) {
            if (e.SocketError != SocketError.Success) {
                LogError("接收数据出错 : " + e.SocketError);
                Disconnect(e.SocketError);
            } else {
                while (m_RecvTokenSize + e.BytesTransferred >= m_RecvTokenBuffer.Length) {
                    byte[] bytes = new byte[m_RecvTokenBuffer.Length * 2];
                    Array.Copy(m_RecvTokenBuffer, 0, bytes, 0, m_RecvTokenSize);
                    m_RecvTokenBuffer = bytes;
                }
                Array.Copy(e.Buffer, 0, m_RecvTokenBuffer, m_RecvTokenSize, e.BytesTransferred);
                m_RecvTokenSize += e.BytesTransferred;
                try {
                    ParsePackage();
                } catch (System.Exception ex) {
                    LogError("解析数据出错 : " + ex.ToString());
                    Disconnect(SocketError.SocketError);
                    return;
                }
                BeginReceive();
            }
        }
        void ParsePackage() {
            for ( ; ; ) {
                if (m_RecvTokenSize < 4) break;
                int size = BitConverter.ToInt32(m_RecvTokenBuffer, 0) + (m_LengthIncludesLengthFieldLength ? 0 : 4);
                if (m_RecvTokenSize < size) break;
                byte type = m_RecvTokenBuffer[4];
                short msgId = BitConverter.ToInt16(m_RecvTokenBuffer, 5);
                int length = size - 7;
                byte[] buffer = new byte[length];
                Array.Copy(m_RecvTokenBuffer, 7, buffer, 0, length);
                OnRecv(type, msgId, length, buffer);
                m_RecvTokenSize -= size;
                if (m_RecvTokenSize > 0) Array.Copy(m_RecvTokenBuffer, size, m_RecvTokenBuffer, 0, m_RecvTokenSize);
            }
        }
        void Disconnect(SocketError error) {
            m_Connection.Disconnect();
        }
        void OnRecv(byte type, short msgId, int length, byte[] data) {
            m_Connection.OnRecv(type, msgId, length, data);
        }
        void LogError(string error) {
            logger.error(error);
        }
    }
}
