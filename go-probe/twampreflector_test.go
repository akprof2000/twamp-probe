package main

// Минимальный сервер TWAMP для сквозных проверок встроенного зонда twping:
// управляющее соединение в открытом режиме плюс отражатель тестовых пакетов.
//
// Зачем он в тестах: без ответа на управляющем канале клиент twping вообще не
// доходит до тестового UDP-сокета — а именно этот сокет и берёт порт из пула
// пробы. Проверить, что twping и twampy не конкурируют за порты, без такого
// сервера невозможно: половина проверки просто не выполнялась бы.
//
// Реализовано только то, что нужно клиенту: открытый режим без шифрования и
// аутентификации, одна сессия на соединение. Раскладка сообщений — по RFC 5357
// (TWAMP) и RFC 4656 (OWAMP, управляющая часть).

import (
	"encoding/binary"
	"errors"
	"io"
	"net"
	"sync"
	"testing"
	"time"

	"github.com/akprof2000/twping-go/owamp"
)

// Длины управляющих сообщений TWAMP (в пакете owamp они непубличные).
const (
	twGreetingLen      = 64
	twSetupResponseLen = 164
	twServerStartLen   = 48
	twTestRequestLen   = 112
	twAcceptSessionLen = 48
	twStartSessionsLen = 32
	twStartAckLen      = 32
	twStopSessionsLen  = 32
)

// twampReflector — сервер TWAMP на 127.0.0.1 со случайным портом.
type twampReflector struct {
	t    *testing.T
	ln   net.Listener
	wg   sync.WaitGroup
	done chan struct{}

	mu        sync.Mutex
	senders   []int // порты клиентов, с которых приходили тестовые пакеты
	sessions  int   // сколько сессий доведено до отражения пакетов
	reflected int   // сколько пакетов отражено
}

// startTwampReflector поднимает сервер и останавливает его по завершении теста.
func startTwampReflector(t *testing.T) *twampReflector {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("рефлектор TWAMP не поднялся: %v", err)
	}

	r := &twampReflector{t: t, ln: ln, done: make(chan struct{})}
	r.wg.Add(1)
	go r.serve()
	t.Cleanup(r.Close)
	return r
}

// Addr возвращает адрес управляющего канала в виде «127.0.0.1:порт».
func (r *twampReflector) Addr() string { return r.ln.Addr().String() }

// Close останавливает сервер и дожидается завершения всех сессий.
func (r *twampReflector) Close() {
	select {
	case <-r.done:
		return
	default:
	}
	close(r.done)
	_ = r.ln.Close()
	r.wg.Wait()
}

// SenderPorts возвращает порты, с которых клиенты слали тестовые пакеты, —
// то есть локальные порты зондов, увиденные с другой стороны провода.
func (r *twampReflector) SenderPorts() []int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return append([]int(nil), r.senders...)
}

// Sessions сообщает число сессий, дошедших до обмена тестовыми пакетами.
func (r *twampReflector) Sessions() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.sessions
}

func (r *twampReflector) serve() {
	defer r.wg.Done()
	for {
		conn, err := r.ln.Accept()
		if err != nil {
			return // сервер остановлен
		}
		r.wg.Add(1)
		go func() {
			defer r.wg.Done()
			defer conn.Close()
			if err := r.session(conn); err != nil && !r.stopping() {
				r.t.Logf("рефлектор TWAMP: %v", err)
			}
		}()
	}
}

func (r *twampReflector) stopping() bool {
	select {
	case <-r.done:
		return true
	default:
		return false
	}
}

// session ведёт одно управляющее соединение целиком: приветствие, согласование
// режима, запрос сессии, обмен тестовыми пакетами и остановку.
func (r *twampReflector) session(conn net.Conn) error {
	// --- Server-Greeting: предлагаем только открытый режим ---------------
	var greeting [twGreetingLen]byte
	binary.BigEndian.PutUint32(greeting[12:16], owamp.ModeOpen)
	if _, err := conn.Write(greeting[:]); err != nil {
		return err
	}

	// --- Set-Up-Response -------------------------------------------------
	var resp [twSetupResponseLen]byte
	if _, err := io.ReadFull(conn, resp[:]); err != nil {
		return err
	}
	if mode := binary.BigEndian.Uint32(resp[0:4]); mode != owamp.ModeOpen {
		return errors.New("клиент выбрал не открытый режим")
	}

	// --- Server-Start ----------------------------------------------------
	var start [twServerStartLen]byte
	start[15] = byte(owamp.AcceptOK)
	owamp.Timestamp{Time: owamp.Num64FromTime(time.Now())}.EncodeTime(start[32:40])
	if _, err := conn.Write(start[:]); err != nil {
		return err
	}

	// --- Request-TW-Session ----------------------------------------------
	var req [twTestRequestLen]byte
	if _, err := io.ReadFull(conn, req[:]); err != nil {
		return err
	}
	if req[0] != owamp.ReqTestTW {
		return errors.New("ожидался Request-TW-Session")
	}

	// Тестовый сокет отражателя — с портом от ядра: пул пробы он занимать
	// не должен, иначе проверка «зонды не конкурируют» проверяла бы не то.
	udp, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		return err
	}
	defer udp.Close()

	var accept [twAcceptSessionLen]byte
	accept[0] = byte(owamp.AcceptOK)
	binary.BigEndian.PutUint16(accept[2:4], uint16(udp.LocalAddr().(*net.UDPAddr).Port))
	if _, err := conn.Write(accept[:]); err != nil {
		return err
	}

	// --- Start-Sessions ---------------------------------------------------
	var startReq [twStartSessionsLen]byte
	if _, err := io.ReadFull(conn, startReq[:]); err != nil {
		return err
	}
	if startReq[0] != owamp.ReqStartSessions {
		return errors.New("ожидался Start-Sessions")
	}
	var ack [twStartAckLen]byte
	ack[0] = byte(owamp.AcceptOK)
	if _, err := conn.Write(ack[:]); err != nil {
		return err
	}

	r.mu.Lock()
	r.sessions++
	r.mu.Unlock()

	reflectDone := make(chan struct{})
	go func() {
		defer close(reflectDone)
		r.reflect(udp)
	}()

	// --- Stop-Sessions ----------------------------------------------------
	var stop [twStopSessionsLen]byte
	_, err = io.ReadFull(conn, stop[:])
	udp.Close()
	<-reflectDone
	return err
}

// reflect отражает тестовые пакеты в раскладке открытого режима TWAMP.
func (r *twampReflector) reflect(udp *net.UDPConn) {
	clock := owamp.Clock{Sync: true, ErrUsec: 100}
	in := make([]byte, 65536)
	reply := make([]byte, owamp.TestTWPayloadSize(owamp.ModeOpen, 0))
	minimum := int(owamp.TestPayloadSize(owamp.ModeOpen, 0))

	var reflSeq uint32
	seen := false
	for {
		n, from, err := udp.ReadFromUDP(in)
		if err != nil {
			return
		}
		recvTS := clock.StampAt(time.Now())
		if n < minimum {
			continue
		}
		if !seen {
			seen = true
			r.mu.Lock()
			r.senders = append(r.senders, from.Port)
			r.mu.Unlock()
		}

		pkt := in[:n]
		senderSeq := binary.BigEndian.Uint32(pkt[0:4])
		sendTime, sendErr := pkt[4:12], pkt[12:14]

		sendTS, _ := clock.Now()
		for i := range reply {
			reply[i] = 0
		}
		binary.BigEndian.PutUint32(reply[0:4], reflSeq)
		sendTS.EncodeTime(reply[4:12])
		sendTS.EncodeErrEstimate(reply[12:14])
		recvTS.EncodeTime(reply[16:24])
		binary.BigEndian.PutUint32(reply[24:28], senderSeq)
		copy(reply[28:36], sendTime)
		copy(reply[36:38], sendErr)
		reply[40] = 64 // TTL отправителя, каким его увидел отражатель
		reflSeq++

		if _, err := udp.WriteToUDP(reply, from); err != nil {
			return
		}
		r.mu.Lock()
		r.reflected++
		r.mu.Unlock()
	}
}
