<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:fo="http://www.w3.org/1999/XSL/Format" xmlns:dl="urn:BimHouse:DrawingList" xmlns:ct="urn:BimHouse:CommonDataType" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:bf='urn:BimHouse:XslFunctions'>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/NewElementResolverStyles.xsl"/>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/PersonStyles.xsl"/>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/AddressStyles.xsl"/>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/OrgStyles.xsl"/>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/ConstructionObjectStyles.xsl"/>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/DocumentStyles.xsl"/>
	<xsl:output method="xml" version="1.0" encoding="UTF-8" indent="yes"/>

	<xsl:template match="processing-instruction()[name() = 'xml-stylesheet']"/>

	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.ФайлОбщихДанных')]" mode="presenting"/>

</xsl:stylesheet>