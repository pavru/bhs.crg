<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet
	version='3.0'
	xmlns:xsl='http://www.w3.org/1999/XSL/Transform'
	xmlns:xs='http://www.w3.org/2001/XMLSchema'
	xmlns:fn='http://www.w3.org/2005/xpath-functions'
	xmlns:ct='urn:BimHouse:CommonDataType'
	xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'
	xmlns:bf='urn:BimHouse:XslFunctions'>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/NewElementResolverStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/PersonStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/OrgStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/ConstructionObjectStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/DocumentStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/MeasuringDeviceStyles.xsl'/>
	<xsl:include href='file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/AddressStyles.xsl'/>
	<xsl:output
		method='xml'
		version='1.0'
		encoding='UTF-8'
		indent='yes'/>
	<xsl:template match='//processing-instruction()'/>
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.ФайлБазовыхДанных')]" mode='presenting'/>
	<xsl:template match='//ct:КабельнаяЛиниия/ct:ПорядковыйНомер' mode='presenting'>
		<xsl:element name='ПорядковыйНомер' namespace='urn:BimHouse:CommonDataType'>
			<xsl:copy-of select='fn:count(../preceding-sibling::ct:КабельнаяЛиниия)+1'/>
		</xsl:element>
	</xsl:template>
</xsl:stylesheet>