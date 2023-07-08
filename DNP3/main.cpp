/*
 * Licensed to Green Energy Corp (www.greenenergycorp.com) under one or
 * more contributor license agreements. See the NOTICE file distributed
 * with this work for additional information regarding copyright ownership.
 * Green Energy Corp licenses this file to you under the Apache License,
 * Version 2.0 (the "License"); you may not use this file except in
 * compliance with the License.  You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
 * This project was forked on 01/01/2013 by Automatak, LLC and modifications
 * may have been made to this file. Automatak, LLC licenses these modifications
 * to you under the terms of the License.
 *
 * Modified and integrated into OSHMI by Ricardo Lastra Olsen (2016-2023).
 *
 */

#define DRIVER_VERSION "OSHMI DNP3 DRIVER V0.80 (Based on Open DNP3 3.1.2) - Copyright 2016-2023 - Ricardo Lastra Olsen"

#include <opendnp3/DNP3Manager.h>
#include <opendnp3/master/PrintingSOEHandler.h>
#include <opendnp3/ConsoleLogger.h>
#include <opendnp3/master/DefaultMasterApplication.h>
#include <opendnp3/master/PrintingCommandResultCallback.h>
#include <opendnp3/channel/PrintingChannelListener.h>

#include <opendnp3/logging/LogLevels.h>
#include <opendnp3/app/ControlRelayOutputBlock.h>

#include <algorithm>
#include <cctype>
#include <thread>
#include <vector>
#include <string>
#include <iostream>
#include <conio.h>

#include "cpp/INIReader.h"
#include "MySOEHandler.h"

#define KEEP_ALIVE_TIME 5
#define MAX_SLAVES 50

using namespace std;
using namespace opendnp3;

std::string to_lower(std::string str)
{
    std::transform(str.begin(), str.end(), str.begin(), [](unsigned char c) { return std::tolower(c); });
    return str;
}

class MyChannelListener final : public IChannelListener, private opendnp3::Uncopyable
  {
    protected:
		int cntSlaves;
		std::shared_ptr<MySOEHandler> soeHandler;

	public:
     virtual void OnStateChange(opendnp3::ChannelState state) override
     {
         std::cout << "channel state change: " << cntSlaves << " " << ChannelStateSpec::to_human_string(state)
                   << std::endl;
		 soeHandler->SendOSHMIChannelStatePoint(state == ChannelState::OPEN);
     }
     static std::shared_ptr<IChannelListener> Create()
     {
         return std::make_shared<MyChannelListener>();
     }
	 void setSOEH(int cntslaves, const std::shared_ptr<MySOEHandler>& sh) {
		 cntSlaves = cntslaves;
		 soeHandler = sh;
	 }
     MyChannelListener()
     {
         cntSlaves = 0;
     }
  };
	
// wait while receiving keep alive messages
void waittobe_active(int shandle)
{
	int seconds_wait = (int)(KEEP_ALIVE_TIME * 3.5);
	time_t now;
	time(&now);
	time_t timeant = now;

	std::cout << "x - exits program" << std::endl;

	while (1)
	{
		unsigned char packet_data[1500];
		unsigned int max_packet_size =
			sizeof(packet_data);

#if PLATFORM == PLATFORM_WINDOWS
		typedef int socklen_t;
#endif
		sockaddr_in from;
		socklen_t fromLength = sizeof(from);

		int bytes = recvfrom(shandle,
			(char*)packet_data,
			max_packet_size,
			0,
			(sockaddr*)&from,
			&fromLength);

		if (bytes > 0)
		{
			unsigned int from_address =
				ntohl(from.sin_addr.s_addr);
			unsigned int from_port =
				ntohs(from.sin_port);

			t_msgcmd * pmsg = (t_msgcmd *)packet_data;

			if (pmsg->signature == MSGKA_SIG)
			{ // message from other active driver, keep waiting
				timeant = now;
			}
		}

		time(&now);

		if (difftime(now, timeant) > seconds_wait)
		{ // passou o tempo sem receber mensagens, retorna para ativar
			return;
		}

		char cmd = 0;
		if (_kbhit())
		{
			cmd = _getch();
		}

		switch (cmd)
		{
		case('x'):
			exit(0);
		}
	}

}

int main(int argc, char* argv[])
{
	vector <shared_ptr <opendnp3::IChannel>> pChannelVec;
	MasterStackConfig stackConfigVec[MAX_SLAVES];
	MySOEHandler soehArr[MAX_SLAVES];
	vector <shared_ptr <MySOEHandler>> spSoehVec;
    vector<shared_ptr<opendnp3::IMaster>> spMasterVec;

	vector<shared_ptr<opendnp3::IMasterScan>> spIntegrityScanVec;
    vector<shared_ptr<opendnp3::IMasterScan>> spExceptionScanVec;
    vector<shared_ptr<opendnp3::IMasterScan>> spRangeScan1Vec;
    vector<shared_ptr<opendnp3::IMasterScan>> spRangeScan2Vec;
    vector<shared_ptr<opendnp3::IMasterScan>> spRangeScan3Vec;
    vector<shared_ptr<opendnp3::IMasterScan>> spRangeScan4Vec;
	int masterAddress, slaveAddress;

	std::cout << DRIVER_VERSION << std::endl;

	INIReader reader_hmi(OSHMIINI);
    if (reader_hmi.ParseError() == -1)
    {
        cout << "OSHMI Ini file not found! " << OSHMIINI << endl;
    }
        
    string IPAddrRed = reader_hmi.GetString("REDUNDANCY", "OTHER_HMI_IP", ""); // ip address of redundant hmi

	INIReader reader(DNP3INI);

	if (reader.ParseError() == -1)
    {
        cout << "DNP3 Ini file not found! " << DNP3INI << endl;
        exit(1);
    }        

	masterAddress = reader.GetInteger("MASTER", "LINK_ADDRESS", 1);
	if (masterAddress > 65534)
		exit(1);

	std::cout << "Starting inactive mode..." << std::endl;
	if (IPAddrRed != "") // se tiver redundancia, espera ficar ativo (sem receber mensagem de keep alive por um tempo)
		waittobe_active(soehArr[0].shandle);

	std::cout << "Entering active mode..." << std::endl;

	// Specify what log levels to use. NORMAL is warning and above
	// You can add all the comms logging by uncommenting below
	const opendnp3::LogLevels FILTERS = levels::NORMAL | levels::ALL_COMMS;

	// This is the main point of interaction with the stack
	DNP3Manager manager(std::thread::hardware_concurrency(), ConsoleLogger::Create());

	int cntslaves = 0;
	char buffer[20];
	string IPAddrAnt, IPAddr;
	uint16_t IPPort, IPPortAnt;
    string SerialPortAnt;
	string range_scan1;
	string range_scan2;
	string range_scan3;
	string range_scan4;

	while ((slaveAddress = reader.GetInteger(((string)"SLAVE") + _itoa(1 + cntslaves, buffer, 10), "LINK_ADDRESS", 65535)) <= 65534)
	{
        IPAddr = reader.GetString(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "IP_ADDRESS", "");
        IPPort = reader.GetInteger(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "IP_PORT", 20000);
        int enable_unsolicited = reader.GetInteger(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "ENABLE_UNSOLICITED", 1);
        int integrity_scan = reader.GetInteger(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "INTEGRITY_SCAN", 180);
        int class0_scan = reader.GetInteger(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "CLASS0_SCAN", 8);
        int class1_scan = reader.GetInteger(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "CLASS1_SCAN", 5);
        int class2_scan = reader.GetInteger(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "CLASS2_SCAN", 17);
        int class3_scan = reader.GetInteger(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "CLASS3_SCAN", 29);
		int response_timeout = reader.GetInteger(((string)"SLAVE") + _itoa(1 + cntslaves, buffer, 10), "RESPONSE_TIMEOUT", 2);
        int time_sync = reader.GetInteger(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "TIME_SYNC", 1);
		int nodata_timeout = reader.GetInteger(((string)"SLAVE") + _itoa(1 + cntslaves, buffer, 10), "NODATA_TIMEOUT", 300);
        string range_scan1 = reader.GetString(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "RANGE_SCAN_1", "");
        string range_scan2 = reader.GetString(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "RANGE_SCAN_2", "");
        string range_scan3 = reader.GetString(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "RANGE_SCAN_3", "");
        string range_scan4 = reader.GetString(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "RANGE_SCAN_4", "");

		// optional serial port config (will be used if port name defined)
		string serial_port_name = reader.GetString(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "SERIAL_PORT_NAME", "");
        int baud_rate = reader.GetInteger(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "BAUD_RATE", 9600);
        int async_open_delay = reader.GetInteger(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "ASYNC_OPEN_DELAY", 0);
        int data_bits = reader.GetInteger(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "DATA_BITS", 8);
        string stop_bits = reader.GetString(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "STOP_BITS", "ONE");
        string parity = reader.GetString(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "PARITY", "NONE");
        string flow_control = reader.GetString(((string) "SLAVE") + _itoa(1 + cntslaves, buffer, 10), "FLOW_CONTROL", "NONE");

		if (IPAddr == "" && serial_port_name == "")
        {
            cout << "Invalid slave config (must have ip adrress or serial port defined!";
            exit(2);
        }		

		soehArr[cntslaves].SetSlaveNo(cntslaves);
		soehArr[cntslaves].SetNoDataTimeout(nodata_timeout);
		soehArr[cntslaves].SetMasterAddress(masterAddress);
		soehArr[cntslaves].SetSlaveAddress(slaveAddress);
		if (IPAddrRed != "")
			soehArr[cntslaves].SetRedundantHMIAddress(IPAddrRed.c_str());
		spSoehVec.push_back(make_shared<MySOEHandler>(soehArr[cntslaves]));

		// one channel per IP/Port or serial port 
		// while the same IP/Port or serial, will use the same channel.
		// Slaves of same IP/Port or serial port must be in sequence on dnp3.ini!
		if ( (serial_port_name == "" && (IPAddr == IPAddrAnt && IPPort == IPPortAnt)) || 
			 (serial_port_name != "" && (serial_port_name == SerialPortAnt))
			) 
	    {
			pChannelVec.push_back(pChannelVec[cntslaves - 1]);
		}
		else
		{
			std::shared_ptr<MyChannelListener> mcl = std::make_shared<MyChannelListener>();
			mcl->setSOEH(cntslaves, spSoehVec[cntslaves]);

			if (serial_port_name != "")
            {
                std::cout << "Using serial port: " << serial_port_name << std::endl;              

                SerialSettings serial_settings;
                serial_settings.deviceName = serial_port_name;
                serial_settings.dataBits = data_bits;
                serial_settings.asyncOpenDelay = TimeDuration::Milliseconds(async_open_delay);
                serial_settings.baud = baud_rate;
                if (to_lower(flow_control) == "hardware")
                    serial_settings.flowType = FlowControl::Hardware;
                else if (to_lower(flow_control) == "xonxoff")
                    serial_settings.flowType = FlowControl::XONXOFF;
                else
                    serial_settings.flowType = FlowControl::None;
                if (to_lower(parity) == "even")
                    serial_settings.parity = Parity::Even;
                else
					serial_settings.parity = Parity::None;
                if (to_lower(stop_bits) == "none")
                    serial_settings.stopBits = StopBits::None;
                else if (to_lower(stop_bits) == "onepointfive")
                    serial_settings.stopBits = StopBits::One;
                else if (to_lower(stop_bits) == "two")
                    serial_settings.stopBits = StopBits::Two;
                else
                    serial_settings.stopBits = StopBits::One;

                // Connect via a serial to a outstation
                auto ch = manager.AddSerial("serialclient" + std::to_string(cntslaves + 1), FILTERS,
                                            ChannelRetry::Default(), serial_settings, mcl);
				if (ch == nullptr)
                {
                    std::cout << "Error allocating serial port client on: " << serial_port_name;
                    exit(1);
                }
                pChannelVec.push_back(ch);
            }
            else
            {
                // Connect via a TCPClient socket to a outstation
                pChannelVec.push_back(manager.AddTCPClient("tcpclient" + std::to_string(cntslaves + 1), FILTERS,
                                                           ChannelRetry::Default(), {IPEndpoint(IPAddr, IPPort)},
                                                           "0.0.0.0", mcl));
            }
		}

		IPAddrAnt = IPAddr; // save last IP
		IPPortAnt = IPPort; // save last port
        SerialPortAnt = serial_port_name; // save last serial port name

     	// you can override application layer settings for the master here
		// in this example, we've change the application layer timeout to 2 seconds
		stackConfigVec[cntslaves].master.responseTimeout = TimeDuration::Seconds(response_timeout);
		stackConfigVec[cntslaves].master.disableUnsolOnStartup = !enable_unsolicited;
        if (!enable_unsolicited)
          stackConfigVec[cntslaves].master.unsolClassMask = ClassField(false, false, false, false);

		if ( time_sync == 1 )
          stackConfigVec[cntslaves].master.timeSyncMode = TimeSyncMode::NonLAN;
		else
		if (time_sync == 2)
      	  stackConfigVec[cntslaves].master.timeSyncMode = TimeSyncMode::LAN;
		else
		  stackConfigVec[cntslaves].master.timeSyncMode = TimeSyncMode::None;
		
		// You can override the default link layer settings here
		// in this example we've changed the default link layer addressing
		stackConfigVec[cntslaves].link.LocalAddr = masterAddress;
		stackConfigVec[cntslaves].link.RemoteAddr = slaveAddress;

		// Um mestre para cada escravo

		// Create a new master on a previously declared port, with a
		// name, log level, command acceptor, and config info. This
		// returns a thread-safe interface used for sending commands.
		std::string master_name = "RTU # " + std::to_string(cntslaves+1) + " Addr: " + std::to_string(slaveAddress);
		spMasterVec.push_back(
			pChannelVec[cntslaves]->AddMaster(
				master_name, // id for logging
				spSoehVec[cntslaves], // callback for data processing                
				opendnp3::DefaultMasterApplication::Create(),	// master application instance
				stackConfigVec[cntslaves] // stack configuration
			));

		// do an integrity poll (Class 3/2/1/0) every N seconds
		if (integrity_scan == 0)
			integrity_scan = 0x7FFFFFFF;
        spIntegrityScanVec.push_back(spMasterVec[cntslaves]->AddClassScan(
            ClassField::AllClasses(), TimeDuration::Seconds(integrity_scan), spSoehVec[cntslaves]));

		// do a Class 1 exception poll every X seconds
		if (class0_scan == 0)
			class0_scan = 0x7FFFFFFF;
        spExceptionScanVec.push_back(spMasterVec[cntslaves]->AddClassScan(
            ClassField(ClassField::CLASS_0), TimeDuration::Seconds(class0_scan), spSoehVec[cntslaves]));

		// do a Class 1 exception poll every X seconds
		if (class1_scan == 0)
			class1_scan = 0x7FFFFFFF;
        spExceptionScanVec.push_back(spMasterVec[cntslaves]->AddClassScan(
            ClassField(ClassField::CLASS_1), TimeDuration::Seconds(class1_scan), spSoehVec[cntslaves]));

		// do a Class 2 exception poll every Y seconds
		if (class2_scan == 0)
			class2_scan = 0x7FFFFFFF;
        spExceptionScanVec.push_back(spMasterVec[cntslaves]->AddClassScan(
            ClassField(ClassField::CLASS_2), TimeDuration::Seconds(class2_scan), spSoehVec[cntslaves]));

		// do a Class 3 exception poll every X seconds
		if (class3_scan == 0)
			class3_scan = 0x7FFFFFFF;
        spExceptionScanVec.push_back(spMasterVec[cntslaves]->AddClassScan(
            ClassField(ClassField::CLASS_3), TimeDuration::Seconds(class3_scan), spSoehVec[cntslaves]));

		if (range_scan1 != "")
		{
			stringstream ss(range_scan1);
			string sub;
			getline(ss, sub, ','); int group = std::stoi(sub);
			getline(ss, sub, ','); int variation = std::stoi(sub);
			getline(ss, sub, ','); int start = std::stoi(sub);
			getline(ss, sub, ','); int stop = std::stoi(sub);
			getline(ss, sub, ','); int period = std::stoi(sub);
			if (period == 0)
				period = 0x7FFFFFFF;
            spRangeScan1Vec.push_back(spMasterVec[cntslaves]->AddRangeScan(
                GroupVariationID(group, variation), start, stop, TimeDuration::Seconds(period), spSoehVec[cntslaves]));
		}
		else
            spRangeScan1Vec.push_back(spMasterVec[cntslaves]->AddRangeScan(
                GroupVariationID(1, 0), 0, 0, TimeDuration::Seconds(0x7FFFFFFF), spSoehVec[cntslaves]));

		if (range_scan2 != "")
		{
			stringstream ss(range_scan2);
			string sub;
			getline(ss, sub, ','); int group = std::stoi(sub);
			getline(ss, sub, ','); int variation = std::stoi(sub);
			getline(ss, sub, ','); int start = std::stoi(sub);
			getline(ss, sub, ','); int stop = std::stoi(sub);
			getline(ss, sub, ','); int period = std::stoi(sub);
			if (period == 0)
				period = 0x7FFFFFFF;
            spRangeScan2Vec.push_back(spMasterVec[cntslaves]->AddRangeScan(
                GroupVariationID(group, variation), start, stop, TimeDuration::Seconds(period), spSoehVec[cntslaves]));
		}
		else
            spRangeScan2Vec.push_back(spMasterVec[cntslaves]->AddRangeScan(
                GroupVariationID(1, 0), 0, 0, TimeDuration::Seconds(0x7FFFFFFF), spSoehVec[cntslaves]));

		if (range_scan3 != "")
		{
			stringstream ss(range_scan3);
			string sub;
			getline(ss, sub, ','); int group = std::stoi(sub);
			getline(ss, sub, ','); int variation = std::stoi(sub);
			getline(ss, sub, ','); int start = std::stoi(sub);
			getline(ss, sub, ','); int stop = std::stoi(sub);
			getline(ss, sub, ','); int period = std::stoi(sub);
			if (period == 0)
				period = 0x7FFFFFFF;
            spRangeScan3Vec.push_back(spMasterVec[cntslaves]->AddRangeScan(
                GroupVariationID(group, variation), start, stop, TimeDuration::Seconds(period), spSoehVec[cntslaves]));
		}
		else
            spRangeScan3Vec.push_back(spMasterVec[cntslaves]->AddRangeScan(
                GroupVariationID(1, 0), 0, 0, TimeDuration::Seconds(0x7FFFFFFF), spSoehVec[cntslaves]));

		if (range_scan4 != "")
		{
			stringstream ss(range_scan4);
			string sub;
			getline(ss, sub, ','); int group = std::stoi(sub);
			getline(ss, sub, ','); int variation = std::stoi(sub);
			getline(ss, sub, ','); int start = std::stoi(sub);
			getline(ss, sub, ','); int stop = std::stoi(sub);
			getline(ss, sub, ','); int period = std::stoi(sub);
			if (period == 0)
				period = 0x7FFFFFFF;
            spRangeScan4Vec.push_back(spMasterVec[cntslaves]->AddRangeScan(
                GroupVariationID(group, variation), start, stop, TimeDuration::Seconds(period), spSoehVec[cntslaves]));
		}
		else
            spRangeScan4Vec.push_back(spMasterVec[cntslaves]->AddRangeScan(
                GroupVariationID(1, 0), 0, 0, TimeDuration::Seconds(0x7FFFFFFF), spSoehVec[cntslaves]));

		// Enable the master. This will start communications.
		spMasterVec[cntslaves]->Enable();

		cntslaves++;

		// when no more slaves found
		if (reader.GetInteger(((string)"SLAVE") + _itoa(1 + cntslaves, buffer, 10), "LINK_ADDRESS", -1) == -1)
		{
			time_t timeant = 0;

			// keep receiving UDP packets
			while (true)
			{
				char cmd = 0;
				if (_kbhit())
				{
					std::cout << "Enter a command" << std::endl;
					std::cout << "x - exits program" << std::endl;
					std::cout << "a - about program" << std::endl;
					std::cout << "i - integrity demand scan" << std::endl;
					std::cout << "e - exception demand scan" << std::endl;
					std::cout << "r - restart connections" << std::endl;
					cmd = _getch();
				}

				switch (cmd)
				{
				case('x'):
					// C++ destructor on DNP3Manager cleans everything up for you
					return 0;
				case('a'):
					std::cout << DRIVER_VERSION << std::endl;
					break;
				case('i'):
					for (int i = 0; i < cntslaves; i++)
						spIntegrityScanVec[i]->Demand();
					break;
				case('e'):
					for (int i = 0; i < cntslaves; i++)
						spExceptionScanVec[i]->Demand();
					break;
				case('r'):
					for (int i = 0; i < cntslaves; i++)
						spMasterVec[i]->Disable();
					Sleep(2000);
					for (int i = 0; i < cntslaves; i++)
						spMasterVec[i]->Enable();
					break;
				case 0:
					break;
				default:
					std::cout << "Unknown action: " << cmd << std::endl;
					break;
				}

				time_t now;
				time(&now);
				if (difftime(now, timeant) > 5)
				{
					timeant = now;
					if (IPAddrRed != "")
						spSoehVec[0]->SendKeepAlive();
					for (int i = 0; i < cntslaves; i++)
					{
						spSoehVec[i]->NoDataTimeoutCnt(5);
						if (spSoehVec[i]->NoDataState)
						{
							spSoehVec[i]->NoDataCntTime = 0;
							spMasterVec[i]->Disable();
							Sleep(500);
							spMasterVec[i]->Enable();
							spSoehVec[i]->NoDataState = false;
						}
					}
				}

				unsigned char packet_data[1500];
				unsigned int max_packet_size = sizeof(packet_data);

#if PLATFORM == PLATFORM_WINDOWS
				typedef int socklen_t;
#endif
				sockaddr_in from;
				socklen_t fromLength = sizeof(from);

				int bytes = recvfrom(spSoehVec[0]->shandle,
					(char*)packet_data,
					max_packet_size,
					0,
					(sockaddr*)&from,
					&fromLength);

				if (bytes <= 0)
					continue;
				unsigned int from_address =
					ntohl(from.sin_addr.s_addr);
				unsigned int from_port =
					ntohs(from.sin_port);

				t_msgcmd * pmsg = (t_msgcmd *)packet_data;

				if (pmsg->signature == MSGKA_SIG)
				{ // message from other active driver, should not happen, exit
					std::cout << "Message from other active driver! Exiting..." << std::endl;
					exit(1);
				}
				else
					if (pmsg->signature == MSGCMD_SIG)
						switch (pmsg->tipo)
						{
						case 45:; // digital single command
						{						
                            OperationType ot = OperationType::NUL;
                            TripCloseCode tc = TripCloseCode::NUL;
							switch (pmsg->qu)
							{
                            case 0:
							default:
								if (pmsg->onoff)
                                    ot = OperationType::LATCH_ON;
								else
                                    ot = OperationType::LATCH_OFF;
								break;
                            case 65:
                                if (pmsg->onoff)
                                {
                                    ot = OperationType::PULSE_ON;
                                    tc = TripCloseCode::CLOSE;
                                }                                   
								else
                                {
                                    ot = OperationType::PULSE_ON;
                                    tc = TripCloseCode::TRIP;
                                }                                    
								break;
                            case 1:
                                if (pmsg->onoff)
                                    ot = OperationType::PULSE_ON;
                                else
                                    ot = OperationType::PULSE_OFF;                               
								break;
							}
							for (int i = 0; i < cntslaves; i++)
							{
								if (stackConfigVec[i].link.RemoteAddr == pmsg->utr) // encontra a utr
								{
									ControlRelayOutputBlock crob(ot, tc);
									auto handler = [&pmsg, &stackConfigVec, &i, &spSoehVec](const ICommandTaskResult& result) -> void
									{
										std::cout << "Summary: " << TaskCompletionSpec::to_human_string(result.summary) << std::endl;
										auto print = [&pmsg, &stackConfigVec, &i, &spSoehVec](const CommandPointResult& res)
										{
											std::cout << " OSHMI Cmd-> RTU:" << pmsg->utr;
											// retorna confirmação como asdu de comando para cima
											t_msgsup msg;
											msg.causa = (res.status == CommandStatus::SUCCESS) ? 0x00 : 0x40;
											msg.endereco = pmsg->endereco;
											msg.tipo = pmsg->tipo;
											msg.prim = stackConfigVec[i].link.LocalAddr;
											msg.sec = stackConfigVec[i].link.RemoteAddr;
											msg.signature = MSGSUP_SIG;
											msg.taminfo = 1;
											msg.info[0] = pmsg->onoff;
											int packet_size = sizeof(int) * 7 + 1;
											spSoehVec[i]->SendOSHMI(&msg, packet_size);

											std::cout
												<< "  Header: " << res.headerIndex
												<< "   Index: " << res.index
												<< "   State: " << CommandPointStateSpec::to_human_string(res.state)
												<< "   Status: " << CommandStatusSpec::to_human_string(res.status);
										};
										result.ForeachItem(print);
									};

									int index = pmsg->endereco - OFFSET_DIGITAL_OUT;

									if (index == 3999) // on demand integrity scan by a command
									{
										spIntegrityScanVec[i]->Demand();
									}
									else
										if (index == 3998) // on demand range 1 scan by a command
										{
											spRangeScan1Vec[i]->Demand();
										}
										else
											if (index == 3997) // on demand range 2 scan by a command
											{
												spRangeScan2Vec[i]->Demand();
											}
											else
												if (index == 3996) // on demand range 3 scan by a command
												{
													spRangeScan3Vec[i]->Demand();
												}
												else
													if (index == 3995) // on demand range 4 scan by a command
													{
														spRangeScan4Vec[i]->Demand();
													}
													else
													{
														if (pmsg->sbo)
															spMasterVec[i]->SelectAndOperate(crob, index, handler);
														else
															spMasterVec[i]->DirectOperate(crob, index, handler);
														break;
													}
								}
							}
						}
						break;
						case 49: // setpoint scaled, will follow as DNP3 signed 16bit signed analog output
							for (int i = 0; i < cntslaves; i++)
							{
								if (stackConfigVec[i].link.RemoteAddr == pmsg->utr) // encontra a utr
								{
									AnalogOutputInt16 ao((int16_t)pmsg->setpoint_i16);
									auto handler = [&pmsg, &stackConfigVec, &i, &spSoehVec](const ICommandTaskResult& result) -> void
									{
										std::cout << "Summary: " << TaskCompletionSpec::to_human_string(result.summary) << std::endl;
										auto print = [&pmsg, &stackConfigVec, &i, &spSoehVec](const CommandPointResult& res)
										{
											std::cout << " OSHMI Cmd-> RTU:" << pmsg->utr;
											// retorna confirmação como asdu de comando para cima
											t_msgsup msg;
											msg.causa = (res.status == CommandStatus::SUCCESS) ? 0x00 : 0x40;
											msg.endereco = pmsg->endereco;
											msg.tipo = pmsg->tipo;
											msg.prim = stackConfigVec[i].link.LocalAddr;
											msg.sec = stackConfigVec[i].link.RemoteAddr;
											msg.signature = MSGSUP_SIG;
											msg.taminfo = 1;
											msg.info[0] = pmsg->onoff;
											int packet_size = sizeof(int) * 7 + 1;
											spSoehVec[i]->SendOSHMI(&msg, packet_size);

											std::cout
												<< "  Header: " << res.headerIndex
												<< "   Index: " << res.index
												<< "   State: " << CommandPointStateSpec::to_human_string(res.state)
                                                << "   Status: " << CommandStatusSpec::to_human_string(res.status);
										};
										result.ForeachItem(print);
									};
									int index = pmsg->endereco - OFFSET_ANALOG_OUT;
									if (pmsg->sbo)
										spMasterVec[i]->SelectAndOperate(ao, index, handler);
									else
										spMasterVec[i]->DirectOperate(ao, index, handler);
									break;
								}
							}
							break;
						case 50: // setpoint float, will follow as DNP3 single precision analog output
							for (int i = 0; i < cntslaves; i++)
							{
								if (stackConfigVec[i].link.RemoteAddr == pmsg->utr) // encontra a utr
								{
									AnalogOutputFloat32 ao(pmsg->setpoint);
									auto handler = [&pmsg, &stackConfigVec, &i, &spSoehVec](const ICommandTaskResult& result) -> void
									{
                                        std::cout << "Summary: " << TaskCompletionSpec::to_human_string(result.summary)
                                                  << std::endl;
										auto print = [&pmsg, &stackConfigVec, &i, &spSoehVec](const CommandPointResult& res)
										{
											std::cout << " OSHMI Cmd-> RTU:" << pmsg->utr;
											// retorna confirmação como asdu de comando para cima
											t_msgsup msg;
											msg.causa = (res.status == CommandStatus::SUCCESS) ? 0x00 : 0x40;
											msg.endereco = pmsg->endereco;
											msg.tipo = pmsg->tipo;
											msg.prim = stackConfigVec[i].link.LocalAddr;
											msg.sec = stackConfigVec[i].link.RemoteAddr;
											msg.signature = MSGSUP_SIG;
											msg.taminfo = 1;
											msg.info[0] = pmsg->onoff;
											int packet_size = sizeof(int) * 7 + 1;
											spSoehVec[i]->SendOSHMI(&msg, packet_size);

											std::cout
												<< "  Header: " << res.headerIndex
												<< "   Index: " << res.index
                                                << "   State: " << CommandPointStateSpec::to_human_string(res.state)
                                                << "   Status: " << CommandStatusSpec::to_human_string(res.status);
										};
										result.ForeachItem(print);
									};
									int index = pmsg->endereco - OFFSET_ANALOG_OUT;
									if (pmsg->sbo)
										spMasterVec[i]->SelectAndOperate(ao, index, handler);
									else
										spMasterVec[i]->DirectOperate(ao, index, handler);
									break;
								}
							}
							break;
						case 51: // bitstring command 32 bits, will follow as DNP3 signed 32bit signed analog output
							for (int i = 0; i < cntslaves; i++)
							{
								if (stackConfigVec[i].link.RemoteAddr == pmsg->utr) // encontra a utr
								{
									AnalogOutputInt32 ao((int32_t)pmsg->setpoint_i32);
									auto handler = [&pmsg, &stackConfigVec, &i, &spSoehVec](const ICommandTaskResult& result) -> void
									{
                                        std::cout << "Summary: " << TaskCompletionSpec::to_human_string(result.summary)
                                                  << std::endl;
										auto print = [&pmsg, &stackConfigVec, &i, &spSoehVec](const CommandPointResult& res)
										{
											std::cout << " OSHMI Cmd-> RTU:" << pmsg->utr;
											// retorna confirmação como asdu de comando para cima
											t_msgsup msg;
											msg.causa = (res.status == CommandStatus::SUCCESS) ? 0x00 : 0x40;
											msg.endereco = pmsg->endereco;
											msg.tipo = pmsg->tipo;
											msg.prim = stackConfigVec[i].link.LocalAddr;
											msg.sec = stackConfigVec[i].link.RemoteAddr;
											msg.signature = MSGSUP_SIG;
											msg.taminfo = 1;
											msg.info[0] = pmsg->onoff;
											int packet_size = sizeof(int) * 7 + 1;
											spSoehVec[i]->SendOSHMI(&msg, packet_size);

											std::cout
												<< "  Header: " << res.headerIndex
												<< "   Index: " << res.index
                                                << "   State: " << CommandPointStateSpec::to_human_string(res.state)
                                                << "   Status: " << CommandStatusSpec::to_human_string(res.status);
										};
										result.ForeachItem(print);
									};
									int index = pmsg->endereco - OFFSET_ANALOG_OUT;
									if (pmsg->sbo)
										spMasterVec[i]->SelectAndOperate(ao, index, handler);
									else
										spMasterVec[i]->DirectOperate(ao, index, handler);
									break;
								}
							}
							break;
						} // switch
			} // while
		}
	}

	return 0;
}

